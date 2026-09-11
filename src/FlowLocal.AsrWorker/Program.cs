using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Serial stdio worker: the model, session and native streaming buffers stay resident.
// In: init/start/push(b64 PCM16)/complete/cancel/exit. Out: status/ok/partial/final/error.
const string ModelFile = "nemotron-speech-streaming-en-0.6b-Q4_K_M.gguf";
const string ModelUrl = "https://huggingface.co/handy-computer/nemotron-speech-streaming-en-0.6b-gguf/resolve/8753c0171773b6438abfb2b9bb6b34599175a0a5/" + ModelFile;
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var model = IntPtr.Zero;
var session = IntPtr.Zero;
var active = false;
long sampleCount = 0;
double processingMs = 0;
int partialCount = 0;
double? firstPartialAudioMs = null;
string lastPartial = "";
float[] audioBuffer = [];
var threads = int.TryParse(Environment.GetEnvironmentVariable("FLOWLOCAL_ASR_THREADS"), out var configuredThreads)
    && configuredThreads > 0 ? configuredThreads : Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

void Emit(object value)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(value, json));
    Console.Out.Flush();
}
void Check(int status)
{
    if (status != 0) throw new InvalidOperationException(
        $"Nemotron ASR: {Marshal.PtrToStringUTF8(Native.transcribe_status_string(status))} ({status}).");
}
string Text() => Marshal.PtrToStringUTF8(Native.transcribe_full_text(session))?.Trim() ?? "";

void Begin()
{
    Native.transcribe_stream_reset(session);
    Native.transcribe_run_params_init(out var run);
    run.Task = 0; // transcription, not translation
    run.Timestamps = 0;
    run.Pnc = 0; // native punctuation; no second ASR pass
    Native.transcribe_stream_params_init(out var stream);
    Native.transcribe_parakeet_stream_ext_init(out var extension);
    extension.RightContext = 1; // 80 ms lookahead / 160 ms first streaming chunk (+ frontend margin)
    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<ParakeetStreamExt>());
    try
    {
        Marshal.StructureToPtr(extension, pointer, false);
        stream.Family = pointer; // stream_begin copies the extension
        Check(Native.transcribe_stream_begin(session, in run, in stream));
    }
    finally { Marshal.FreeHGlobal(pointer); }
    active = true;
    sampleCount = 0;
    processingMs = 0;
    partialCount = 0;
    firstPartialAudioMs = null;
    lastPartial = "";
}

async Task InitializeAsync()
{
    if (session != IntPtr.Zero) return;
    if (Marshal.PtrToStringUTF8(Native.transcribe_version()) != "0.2.3")
        throw new InvalidOperationException("This worker requires transcribe.cpp 0.2.3 (matching native ABI).");
    Check(Native.transcribe_init_backends_default());
    var path = Environment.GetEnvironmentVariable("FLOWLOCAL_ASR_MODEL_PATH") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLocal", "Models", ModelFile);
    if (!File.Exists(path))
    {
        if (Environment.GetEnvironmentVariable("FLOWLOCAL_ASR_MODEL_PATH") is not null)
            throw new FileNotFoundException("Configured Nemotron GGUF was not found.", path);
        Emit(new { evt = "status", state = "Downloading", detail = ModelFile });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var sink = File.Create(temp))
                await response.Content.CopyToAsync(sink);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    Emit(new { evt = "status", state = "Loading", detail = ModelFile });
    Native.transcribe_model_load_params_init(out var load);
    load.Backend = 1; // explicit CPU, no hidden GPU fallback
    Check(Native.transcribe_model_load_file(path, in load, out model));
    Native.transcribe_session_params_init(out var parameters);
    parameters.NThreads = threads;
    try
    {
        Check(Native.transcribe_session_init(model, in parameters, out session));
        Begin();
        Check(Native.transcribe_stream_feed(session, new float[8000], 8000, IntPtr.Zero));
        Check(Native.transcribe_stream_finalize(session, IntPtr.Zero));
        Native.transcribe_stream_reset(session);
        active = false;
    }
    catch
    {
        Native.transcribe_session_free(session);
        Native.transcribe_model_free(model);
        session = model = IntPtr.Zero;
        throw;
    }
    Emit(new { evt = "status", state = "Ready", detail = $"transcribe.cpp 0.2.3 CPU · {threads} threads · streaming R=1 · greedy · no VAD" });
}

void Push(string base64)
{
    if (!active) throw new InvalidOperationException("No ASR stream is active.");
    var pcm = Convert.FromBase64String(base64);
    if (pcm.Length % 2 != 0) throw new ArgumentException("PCM16 audio must contain whole samples.");
    if (pcm.Length == 0) return;
    var count = pcm.Length / 2;
    if (audioBuffer.Length < count) audioBuffer = new float[count];
    for (var i = 0; i < count; i++) audioBuffer[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
    var started = Stopwatch.GetTimestamp();
    Check(Native.transcribe_stream_feed(session, audioBuffer, count, IntPtr.Zero));
    processingMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    sampleCount += count;
    var text = Text();
    if (text.Length > 0 && text != lastPartial)
    {
        partialCount++;
        firstPartialAudioMs ??= sampleCount / 16.0;
        lastPartial = text;
        Emit(new { evt = "partial", text, audioMs = sampleCount / 16.0 });
    }
}

void Complete()
{
    if (!active) throw new InvalidOperationException("No ASR stream is active.");
    var started = Stopwatch.GetTimestamp();
    try
    {
        Check(Native.transcribe_stream_finalize(session, IntPtr.Zero));
        var finalizeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Emit(new { evt = "final", text = Text(), metrics = new {
            finalizeMs, processingMs = processingMs + finalizeMs,
            audioMs = sampleCount / 16.0, partialCount, firstPartialAudioMs } });
    }
    finally { active = false; }
}

try
{
    Emit(new { evt = "status", state = "Initializing" });
    while (await Console.In.ReadLineAsync() is { } line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var command = document.RootElement.GetProperty("cmd").GetString();
            switch (command)
            {
                case "init": await InitializeAsync(); Emit(new { evt = "ok" }); break;
                case "start": await InitializeAsync(); Begin(); Emit(new { evt = "ok" }); break;
                case "push": Push(document.RootElement.GetProperty("b64").GetString()!); break;
                case "complete": Complete(); break;
                case "cancel":
                    if (session != IntPtr.Zero) Native.transcribe_stream_reset(session);
                    active = false;
                    Emit(new { evt = "ok" });
                    break;
                case "exit": return;
                default: throw new ArgumentException("Unknown ASR command.");
            }
        }
        catch (Exception exception)
        {
            if (session != IntPtr.Zero) Native.transcribe_stream_reset(session);
            active = false;
            Emit(new { evt = "error", message = exception.Message });
        }
    }
}
finally
{
    Native.transcribe_session_free(session);
    Native.transcribe_model_free(model);
}

internal static class Native
{
    private const string Lib = "transcribe";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern IntPtr transcribe_version();
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern IntPtr transcribe_status_string(int status);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern int transcribe_init_backends_default();
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_model_load_params_init(out ModelLoadParams p);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_session_params_init(out SessionParams p);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_run_params_init(out RunParams p);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_stream_params_init(out StreamParams p);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_parakeet_stream_ext_init(out ParakeetStreamExt p);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern int transcribe_model_load_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, in ModelLoadParams p, out IntPtr model);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern int transcribe_session_init(IntPtr model, in SessionParams p, out IntPtr session);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_session_free(IntPtr session);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_model_free(IntPtr model);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern int transcribe_stream_begin(IntPtr session, in RunParams run, in StreamParams stream);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern int transcribe_stream_feed(IntPtr session, float[] pcm, int count, IntPtr update);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern int transcribe_stream_finalize(IntPtr session, IntPtr update);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern void transcribe_stream_reset(IntPtr session);
    [DllImport(Lib, CallingConvention = Cdecl)] internal static extern IntPtr transcribe_full_text(IntPtr session);
}

// Exact x64 layouts from transcribe.cpp v0.2.3 include/transcribe.h and parakeet.h.
[StructLayout(LayoutKind.Sequential)]
internal struct ModelLoadParams { public ulong Size; public int Backend; public IntPtr Device; }
[StructLayout(LayoutKind.Sequential)]
internal struct SessionParams { public ulong Size; public int NThreads, KvType, NCtx; }
[StructLayout(LayoutKind.Sequential)]
internal struct RunParams
{
    public ulong Size;
    public int Task, Timestamps, Pnc, Itn, Diarize;
    public IntPtr Language, TargetLanguage;
    [MarshalAs(UnmanagedType.U1)] public bool KeepSpecialTags;
    public IntPtr Family;
    public int SpecKDrafts;
}
[StructLayout(LayoutKind.Sequential)]
internal struct StreamParams { public ulong Size; public IntPtr Family; public int CommitPolicy; public uint StablePrefixAgreement; }
[StructLayout(LayoutKind.Sequential)]
internal struct ParakeetStreamExt { public ulong Size; public uint Kind; private uint padding; public int RightContext; }
