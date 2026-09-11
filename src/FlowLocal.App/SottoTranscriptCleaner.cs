using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using FlowLocal.Core;
namespace FlowLocal.App;
public sealed record CleanupMetrics(
    int InputTokens,
    int OutputTokens,
    double? PromptMilliseconds,
    double? TimeToFirstTokenMilliseconds,
    double? DecodeMilliseconds,
    double? DecodeTokensPerSecond,
    int DraftTokens,
    int DraftAcceptedTokens,
    double CompleteMilliseconds,
    long WorkingSetBytes,
    bool DsparkEnabled);

/// <summary>Resident LFM2.5 cleanup server. Set FLOWLOCAL_CLEANUP_DSPARK=1 to attach the Q4_K_M drafter.</summary>
public sealed class SottoTranscriptCleaner : ITranscriptCleaner, ICleanupBackend, IDisposable
{
    private const string ModelPathVariable = "FLOWLOCAL_CLEANUP_MODEL_PATH";
    private const string DsparkPathVariable = "FLOWLOCAL_CLEANUP_DSPARK_MODEL_PATH";
    private const string ServerPathVariable = "FLOWLOCAL_LLAMA_SERVER_PATH";
    private const int Port = 19081;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly SemaphoreSlim inferenceLock = new(1, 1);
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? server;
    private IntPtr serverJob;
    private bool disposed;

    public string BackendId => DsparkEnabled ? "lfm25-1.2b-qad-q4_0-dspark-q4_k_m" : "lfm25-1.2b-qad-q4_0";
    public string DisplayName => DsparkEnabled
        ? "LFM2.5-1.2B QAD Q4_0 + DSpark Q4_K_M"
        : "LFM2.5-1.2B QAD Q4_0";
    public bool IsLoaded => server is { HasExited: false };
    public bool DsparkEnabled => string.Equals(Environment.GetEnvironmentVariable("FLOWLOCAL_CLEANUP_DSPARK"), "1", StringComparison.OrdinalIgnoreCase);
    public string ExecutionTarget { get; private set; } = "not loaded";
    public CleanupMetrics? LastMetrics { get; private set; }
    public static string? ConfiguredModelPath => ResolveModelPath(ModelPathVariable, "LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf");

    public async Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return new BackendAvailability(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BackendAvailability(false, exception.Message);
        }
    }

    public async Task<CleanTranscriptResult> CleanAsync(
        RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/completion")
            {
                Content = JsonContent.Create(new
                {
                    prompt = DictationPromptAdapter.Build(transcript),
                    n_predict = Math.Clamp(transcript.Text.Length / 2, 32, 256),
                    temperature = 0,
                    top_k = 1,
                    top_p = 1,
                    repeat_penalty = 1.05,
                    stop = new[] { "<|im_end|>", "<|endoftext|>" },
                    stream = true,
                    cache_prompt = false,
                    return_tokens = false
                })
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(body);
            var output = new StringBuilder();
            double? firstTokenMs = null;
            double? promptMs = null, decodeMs = null, tokPerSecond = null;
            var inputTokens = 0;
            var outputTokens = 0;
            var draftTokens = 0;
            var draftAccepted = 0;
            var terminalSeen = false;
            var incomplete = false;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var payload = line[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var token = content.GetString() ?? "";
                    if (token.Length > 0) firstTokenMs ??= Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    output.Append(token);
                }
                if (root.TryGetProperty("stop_type", out var stopType) && stopType.ValueKind == JsonValueKind.String)
                {
                    terminalSeen = true;
                    incomplete = string.Equals(stopType.GetString(), "limit", StringComparison.OrdinalIgnoreCase);
                }
                if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
                {
                    terminalSeen = true;
                    incomplete = true;
                }
                if (!root.TryGetProperty("timings", out var timings)) continue;
                inputTokens = ReadInt(timings, "prompt_n");
                outputTokens = ReadInt(timings, "predicted_n");
                promptMs = ReadDouble(timings, "prompt_ms");
                decodeMs = ReadDouble(timings, "predicted_ms");
                tokPerSecond = ReadDouble(timings, "predicted_per_second");
                draftTokens = ReadInt(timings, "draft_n");
                draftAccepted = ReadInt(timings, "draft_n_accepted");
            }
            if (!terminalSeen) throw new InvalidOperationException("Cleanup stream ended before llama.cpp reported completion.");
            if (incomplete) throw new InvalidOperationException("Cleanup output hit the token limit and was discarded.");
            var completeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var process = server;
            LastMetrics = new CleanupMetrics(inputTokens, outputTokens, promptMs, firstTokenMs, decodeMs, tokPerSecond,
                draftTokens, draftAccepted, completeMs, process?.WorkingSet64 ?? 0, DsparkEnabled);
            return new CleanTranscriptResult(output.ToString().Trim());
        }
        finally { inferenceLock.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (IsLoaded) return;
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsLoaded) return;
            StopServer();
            var model = RequireModel(ModelPathVariable, "LFM2.5-1.2B-Instruct-QAD-Q4_0.gguf");
            var packaged = Path.Combine(AppContext.BaseDirectory, "llama", "llama-server.exe");
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlowLocal", "Runtimes", "llama", "llama-server.exe");
            var executable = Environment.GetEnvironmentVariable(ServerPathVariable)
                ?? (File.Exists(packaged) ? packaged : local);
            if (!File.Exists(executable)) throw new FileNotFoundException(
                $"llama-server.exe was not found. Set {ServerPathVariable} to a local llama.cpp server executable.", executable);
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            };
            Add(info, "--model", model);
            Add(info, "--host", "127.0.0.1"); Add(info, "--port", Port.ToString());
            Add(info, "--threads", Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)).ToString());
            Add(info, "--threads-batch", Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)).ToString());
            Add(info, "--ctx-size", "2048"); Add(info, "--parallel", "1"); AddFlag(info, "--no-webui"); AddFlag(info, "--metrics");
            Add(info, "--temp", "0"); Add(info, "--top-k", "1"); Add(info, "--top-p", "1");
            Add(info, "--reasoning-budget", "0"); AddFlag(info, "--log-disable");
            if (DsparkEnabled)
            {
                var draft = RequireModel(DsparkPathVariable, "LFM2.5-1.2B-Instruct-DSpark-Q4_K_M.gguf");
                Add(info, "--model-draft", draft); Add(info, "--spec-type", "draft-dspark");
                Add(info, "--spec-draft-n-max", "9"); Add(info, "--spec-draft-n-min", "0");
            }
            serverJob = CreateServerJob();
            try
            {
                server = Process.Start(info) ?? throw new InvalidOperationException("llama-server could not be started.");
                if (!AssignProcessToJobObject(serverJob, server.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not bind llama-server to the application lifetime.");
                _ = server.StandardError.ReadToEndAsync();
                var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 120);
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (server.HasExited) throw new InvalidOperationException($"llama-server exited during startup (code {server.ExitCode}).");
                    try
                    {
                        using var health = await http.GetAsync($"http://127.0.0.1:{Port}/health", cancellationToken).ConfigureAwait(false);
                        if (health.IsSuccessStatusCode)
                        {
                            ExecutionTarget = DsparkEnabled ? "llama.cpp CPU · DSpark draft-dspark" : "llama.cpp CPU";
                            return;
                        }
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                throw new TimeoutException("llama-server did not become ready within 120 seconds.");
            }
            catch
            {
                StopServer();
                throw;
            }
        }
        finally { initializationLock.Release(); }
    }

    private static void Add(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }
    private static IntPtr CreateServerJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create llama-server job.");
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 }
        };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            CloseHandle(job);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure llama-server job.");
        }
        return job;
    }

    private void StopServer()
    {
        try { if (server is { HasExited: false }) server.Kill(entireProcessTree: true); } catch { }
        server?.Dispose();
        server = null;
        if (serverJob != IntPtr.Zero)
        {
            CloseHandle(serverJob);
            serverJob = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
    private static void AddFlag(ProcessStartInfo info, string name) => info.ArgumentList.Add(name);
    private static string RequireModel(string variable, string fileName) => ResolveModelPath(variable, fileName)
        ?? throw new InvalidOperationException($"Set {variable} to the local GGUF path.");
    private static string? ResolveModelPath(string variable, string fileName)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLocal", "Models", fileName);
        return File.Exists(path) ? path : null;
    }
    private static int ReadInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static double? ReadDouble(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopServer();
        http.Dispose();
        initializationLock.Dispose();
        inferenceLock.Dispose();
    }
}
