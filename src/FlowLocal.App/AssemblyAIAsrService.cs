using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class AssemblyAIAsrService : IAsrService, IDisposable
{
    public const string ModelName = "universal-3-5-pro";
    private const int BytesPerSecond = 32_000;
    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MemoryStream? _audio;
    private AsrSessionOptions? _options;

    public AsrBackendStatus Status { get; private set; } = new(AsrBackendState.NotInstalled);

    public AssemblyAIAsrService(string? apiKey)
        : this(apiKey, new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(3),
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })) { }

    internal AssemblyAIAsrService(string? apiKey, HttpClient client)
    {
        _apiKey = apiKey;
        _client = client;
        _client.BaseAddress = new Uri("https://sync.assemblyai.com/");
        _client.Timeout = TimeSpan.FromSeconds(35);
        _client.DefaultRequestHeaders.Add("X-AAI-Model", ModelName);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            Status = new(AsrBackendState.Failed, ModelName, "AssemblyAI Sync STT", "Add an AssemblyAI API key in Settings > Models and diagnostics, save, then restart.");
            throw new InvalidOperationException(Status.FailureMessage);
        }
        Status = new(AsrBackendState.Ready, ModelName, "AssemblyAI Sync STT");
        _ = WarmAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _client.GetAsync("warm", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort: transcription still makes its own request if pre-warming fails.
        }
    }

    public async Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken)
    {
        if (options.SampleRate != 16_000 || options.BitsPerSample != 16 || options.Channels != 1)
            throw new ArgumentException("AssemblyAI dictation requires 16 kHz mono S16LE PCM.", nameof(options));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_audio is not null) throw new InvalidOperationException("A dictation session is already active.");
            _options = options;
            _audio = new MemoryStream();
            _ = WarmAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var audio = _audio ?? throw new InvalidOperationException("No dictation session is active.");
            if ((pcmAudio.Length & 1) != 0) throw new ArgumentException("PCM audio must contain whole 16-bit samples.", nameof(pcmAudio));
            if (audio.Length + pcmAudio.Length > 120L * BytesPerSecond)
                throw new InvalidOperationException("AssemblyAI Sync STT supports clips up to 120 seconds. Record a shorter dictation.");
            audio.Write(pcmAudio.Span);
        }
        finally { _gate.Release(); }
    }

    public async Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var audio = _audio ?? throw new InvalidOperationException("No dictation session is active.");
            var options = _options!;
            if (audio.Length < BytesPerSecond * 0.08)
                throw new InvalidOperationException("AssemblyAI Sync STT requires at least 80 ms of audio. Hold the shortcut longer.");
            if (string.IsNullOrWhiteSpace(_apiKey)) throw new InvalidOperationException("Add an AssemblyAI API key in Settings > Models and diagnostics, save, then restart.");
            using var form = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(audio.GetBuffer(), 0, checked((int)audio.Length));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("audio/pcm");
            form.Add(bytes, "audio", "dictation.pcm");
            form.Add(JsonContent.Create(new
            {
                sample_rate = options.SampleRate,
                channels = options.Channels,
                prompt = options.RecognitionPrompt ?? AssemblyAIPrompts.Recognition(null, OutputContextCategory.General),
                keyterms_prompt = AssemblyAIPrompts.Keyterms(options.Keyterms ?? []),
                timestamps = false
            }), "config");
            using var request = new HttpRequestMessage(HttpMethod.Post, "transcribe") { Content = form };
            request.Headers.Add("Authorization", _apiKey);
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"AssemblyAI Sync STT failed (HTTP {(int)response.StatusCode}). Check your API key, quota, and connection; retry from History.", null, response.StatusCode);
                using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken).ConfigureAwait(false);
                if (body is null || !body.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("AssemblyAI Sync STT returned an invalid response.");
                return new AsrResult(text.GetString()!);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("AssemblyAI Sync STT timed out. Check your connection and retry from History.");
            }
            finally
            {
                Debug.WriteLine($"[Dictation] STT request: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");
            }
        }
        finally
        {
            _audio?.Dispose();
            _audio = null;
            _options = null;
            _gate.Release();
        }
    }

    public async Task CancelSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _audio?.Dispose();
            _audio = null;
            _options = null;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _client.Dispose();
        _audio?.Dispose();
    }
}
