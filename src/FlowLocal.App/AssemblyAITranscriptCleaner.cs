using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class AssemblyAITranscriptCleaner : ITranscriptCleaner, ICleanupBackend, IDisposable
{
    public const string DefaultModel = "qwen3.5-4b-32k-fast";
    private readonly HttpClient _client;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public string BackendId => "assemblyai-llm-gateway";
    public string DisplayName => $"AssemblyAI LLM Gateway ({_model})";

    public AssemblyAITranscriptCleaner(string? apiKey, string? model = null)
        : this(apiKey, model, new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(3),
            ConnectTimeout = TimeSpan.FromSeconds(3)
        }), TimeSpan.FromSeconds(5)) { }

    internal AssemblyAITranscriptCleaner(string? apiKey, string? model, HttpClient client, TimeSpan timeout)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _client = client;
        _client.BaseAddress = new Uri("https://llm-gateway.assemblyai.com/v1/");
        _timeout = timeout;
    }

    public Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Formatting is optional: a missing key or unavailable model must never disable recognition.
        return Task.FromResult(new BackendAvailability(!string.IsNullOrWhiteSpace(_apiKey), "Add an AssemblyAI API key in Settings > Models and diagnostics, save, then restart; otherwise raw transcripts are inserted."));
    }

    public async Task<CleanTranscriptResult> CleanAsync(RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(transcript.Text)) return new CleanTranscriptResult(transcript.Text);
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return new CleanTranscriptResult(transcript.Text, UsedFallback: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            request.Headers.TryAddWithoutValidation("Authorization", _apiKey);
            request.Content = JsonContent.Create(new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = AssemblyAIPrompts.Rewrite(style, transcript.Category) },
                    new { role = "user", content = transcript.Text }
                },
                temperature = 0,
                max_tokens = Math.Clamp(transcript.Text.Length + 128, 256, 4096)
            });
            using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(timeout.Token).ConfigureAwait(false);
            if (body is null) throw new JsonException("Empty Gateway response.");
            var choice = body!.RootElement.GetProperty("choices")[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop")
                return new CleanTranscriptResult(transcript.Text, UsedFallback: true);
            var text = choice.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
            var cleaned = new CleanTranscriptResult(text);
            var terminal = transcript.Category == OutputContextCategory.Terminal || style.Category.Equals("Terminal", StringComparison.OrdinalIgnoreCase);
            if (!CleanupResultValidator.TryValidate(transcript, cleaned, out _) ||
                (terminal && (text.Contains('\r') || text.Contains('\n') || text.Contains('\u001b'))))
                return new CleanTranscriptResult(transcript.Text, UsedFallback: true);
            return cleaned;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Do not log response bodies, model output, transcripts, or credentials.
            Debug.WriteLine($"[Dictation] Rewrite fallback: {exception.GetType().Name}");
            return new CleanTranscriptResult(transcript.Text, UsedFallback: true);
        }
        finally
        {
            Debug.WriteLine($"[Dictation] LLM rewrite request: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");
        }
    }

    public void Dispose() => _client.Dispose();
}
