using System.Runtime.InteropServices;
using System.Text.Json;

// Headless ASR worker: owns every native GGUF call so stalls or native aborts
// never take FlowLocal.App down. Protocol is line-delimited JSON on stdio.
//
// In:  {"cmd":"init"} | {"cmd":"start"} | {"cmd":"push","b64":...} | {"cmd":"complete"} | {"cmd":"cancel"} | {"cmd":"exit"}
// Out: {"evt":"status","state":"Initializing"|"Downloading"|"Loading"|"Ready","detail":...}
//      {"evt":"ok"} | {"evt":"final","text":...} | {"evt":"error","message":...}
//
// Engine: canary-180m-flash Q4_K_M via transcribe.cpp (handy-computer), forced
// onto the CPU backend. Greedy decoding only; PnC off (Sotto cleans up after);
// no timestamps, translation, or language detection.

const int SampleRate = 16_000;
const string ModelDirName = "canary-180m-flash-gguf";
const string ModelFileName = "canary-180m-flash-Q4_K_M.gguf";
const string ModelRepoUrl = "https://huggingface.co/handy-computer/canary-180m-flash-gguf/resolve/main";

const int BackendCpu = 1;                 // TRANSCRIBE_BACKEND_CPU: exact selection, never falls back
const int TaskTranscribe = 0;             // TRANSCRIBE_TASK_TRANSCRIBE (not TRANSLATE)
const int TimestampsNone = 0;             // TRANSCRIBE_TIMESTAMPS_NONE
const int PncOff = 1;                     // TRANSCRIBE_PNC_MODE_OFF

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var stdout = Console.Out;
var outputLock = new object();
var downloadClient = new HttpClient();

void Emit(string json)
{
    lock (outputLock)
    {
        stdout.WriteLine(json);
        stdout.Flush();
    }
}

void Status(string state, string? detail = null)
{
    Emit(JsonSerializer.Serialize(new StatusEvent("status", state, detail), jsonOptions));
}

IntPtr model = IntPtr.Zero;
IntPtr session = IntPtr.Zero;
List<float>? samples = null;

static string DescribeStatus(int code) => code switch
{
    0 => "ok",
    1 => "invalid argument",
    2 => "not implemented",
    3 => "model file not found",
    4 => "invalid GGUF file",
    5 => "unsupported architecture",
    6 => "unsupported model variant",
    7 => "out of memory",
    8 => "backend unavailable",
    9 => "unsupported sample rate",
    10 => "unsupported language",
    11 => "unsupported task",
    12 => "unsupported timestamps",
    13 => "aborted",
    _ => $"transcribe error {code}",
};

async Task EnsureInitializedAsync()
{
    if (session != IntPtr.Zero) return;

    var dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FlowLocal", "Models", ModelDirName);
    Directory.CreateDirectory(dir);
    // The release build ships ggml backends as modules next to transcribe.dll;
    // point the library at its own directory once so CPU can be selected.
    var brc = Native.transcribe_init_backends_default();
    if (brc != 0) throw new InvalidOperationException($"Native ASR backend registration failed: {DescribeStatus(brc)}.");

    Status("Initializing");

    var target = Path.Combine(dir, ModelFileName);
    if (!File.Exists(target))
    {
        Status("Downloading", ModelFileName);
        var temp = target + ".download";
        using (var response = await downloadClient.GetAsync($"{ModelRepoUrl}/{ModelFileName}", HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var sink = File.Create(temp);
            await source.CopyToAsync(sink);
        }
        File.Move(temp, target);
    }

    Status("Loading");

    // Exact CPU backend request: the library returns an error instead of
    // silently falling back to another device when CPU cannot be honored.
    TranscribeModelLoadParams loadParams = default;
    Native.transcribe_model_load_params_init(ref loadParams);
    loadParams.Backend = BackendCpu;
    var rc = Native.transcribe_model_load_file(target, in loadParams, out model);
    if (rc != 0) throw new InvalidOperationException($"Canary model failed to load: {DescribeStatus(rc)}.");

    TranscribeSessionParams sessionParams = default;
    Native.transcribe_session_params_init(ref sessionParams);
    rc = Native.transcribe_session_init(model, in sessionParams, out session);
    if (rc != 0)
    {
        Native.transcribe_model_free(model);
        model = IntPtr.Zero;
        throw new InvalidOperationException($"Canary session failed to start: {DescribeStatus(rc)}.");
    }

    WarmUp();
    Status("Ready", $"{Variant()} · {Backend()} · greedy · pnc off");
}

/// <summary>One throwaway inference so the first real dictation pays no graph-setup cost.</summary>
void WarmUp()
{
    TranscribeRunParams runParams = default;
    Native.transcribe_run_params_init(ref runParams);
    ApplyAsrParams(ref runParams);
    var silence = new float[SampleRate / 2];
    _ = Native.transcribe_run(session, silence, silence.Length, in runParams);
    Native.transcribe_reset_timings(session);
}

static void ApplyAsrParams(ref TranscribeRunParams p)
{
    p.Task = TaskTranscribe;
    p.Timestamps = TimestampsNone;
    p.Pnc = PncOff;
    p.Language = Native.LanguageEn;
    // target_language stays NULL: transcription only, never translation.
}

string Variant() => Marshal.PtrToStringUTF8(Native.transcribe_model_variant_string(model)) is { Length: > 0 } v ? v : "canary-180m-flash";
string Backend() => Marshal.PtrToStringUTF8(Native.transcribe_model_backend(model)) is { Length: > 0 } b ? b : "cpu";

async Task StartSessionAsync()
{
    if (samples is not null) return;
    await EnsureInitializedAsync();
    samples = [];
    Emit("{\"evt\":\"ok\"}");
}

void PushAudio(string base64)
{
    if (samples is null) throw new InvalidOperationException("No ASR session is active.");
    var pcm = Convert.FromBase64String(base64);
    for (var i = 0; i + 1 < pcm.Length; i += 2)
    {
        samples.Add(BitConverter.ToInt16(pcm, i) / 32768f);
    }
}

void CompleteSession()
{
    if (samples is null) throw new InvalidOperationException("No ASR session is active.");
    var audio = samples.ToArray();
    try
    {
        if (audio.Length < SampleRate / 10)
        {
            Emit("{\"evt\":\"error\",\"message\":\"No speech was recognized.\"}");
            return;
        }

        TranscribeRunParams runParams = default;
        Native.transcribe_run_params_init(ref runParams);
        ApplyAsrParams(ref runParams);
        var rc = Native.transcribe_run(session, audio, audio.Length, in runParams);
        if (rc != 0) throw new InvalidOperationException($"Speech recognition failed: {DescribeStatus(rc)}.");

        var text = Marshal.PtrToStringUTF8(Native.transcribe_full_text(session));
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Emit("{\"evt\":\"error\",\"message\":\"No speech was recognized.\"}");
        }
        else
        {
            Emit(JsonSerializer.Serialize(new FinalEvent("final", text), jsonOptions));
        }
    }
    finally
    {
        samples = null;
    }
}

void CancelSession()
{
    samples = null;
    Emit("{\"evt\":\"ok\"}");
}

Status("Starting");
while (await Console.In.ReadLineAsync() is { } line)
{
    JsonDocument document;
    try { document = JsonDocument.Parse(line); }
    catch (JsonException) { continue; }
    using (document)
    {
        var command = document.RootElement.GetProperty("cmd").GetString();
        try
        {
            switch (command)
            {
                case "init":
                    await EnsureInitializedAsync();
                    Emit("{\"evt\":\"ok\"}");
                    break;
                case "start":
                    await StartSessionAsync();
                    break;
                case "push":
                    PushAudio(document.RootElement.GetProperty("b64").GetString()!);
                    break;
                case "complete":
                    CompleteSession();
                    break;
                case "cancel":
                    CancelSession();
                    break;
                case "exit":
                    return;
            }
        }
        catch (Exception exception)
        {
            var message = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
            Emit(JsonSerializer.Serialize(new ErrorEvent("error", message), jsonOptions));
            if (command is "push" or "start") { samples = null; }
        }
    }
}

internal static partial class Native
{
    private const string Lib = "transcribe";
    private const CallingConvention Cdecl = System.Runtime.InteropServices.CallingConvention.Cdecl;

    internal static readonly IntPtr LanguageEn = Marshal.StringToCoTaskMemUTF8("en");

    /// <summary>Dynamic-backend builds must register their module directory once, before the first model load.</summary>
    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int transcribe_init_backends_default();

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void transcribe_model_load_params_init(ref TranscribeModelLoadParams params_);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void transcribe_session_params_init(ref TranscribeSessionParams params_);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void transcribe_run_params_init(ref TranscribeRunParams params_);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int transcribe_model_load_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        in TranscribeModelLoadParams params_,
        out IntPtr outModel);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void transcribe_model_free(IntPtr model);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int transcribe_session_init(IntPtr model, in TranscribeSessionParams params_, out IntPtr outSession);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int transcribe_run(IntPtr session, float[] pcm, int nSamples, in TranscribeRunParams params_);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern IntPtr transcribe_full_text(IntPtr session);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern IntPtr transcribe_model_variant_string(IntPtr model);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern IntPtr transcribe_model_backend(IntPtr model);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void transcribe_reset_timings(IntPtr session);
}

// Layouts mirror include/transcribe/transcribe.h on x64. struct_size and the
// remaining defaults are filled by the *_init functions, not by us.
[StructLayout(LayoutKind.Explicit)]
internal struct TranscribeModelLoadParams
{
    [FieldOffset(0)] public ulong StructSize;
    [FieldOffset(8)] public int Backend;
    [FieldOffset(16)] public IntPtr Device;
}

[StructLayout(LayoutKind.Explicit)]
internal struct TranscribeSessionParams
{
    [FieldOffset(0)] public ulong StructSize;
    [FieldOffset(8)] public int NThreads;
    [FieldOffset(12)] public int KvType;
    [FieldOffset(16)] public int NCtx;
}

[StructLayout(LayoutKind.Explicit)]
internal struct TranscribeRunParams
{
    [FieldOffset(0)] public ulong StructSize;
    [FieldOffset(8)] public int Task;
    [FieldOffset(12)] public int Timestamps;
    [FieldOffset(16)] public int Pnc;
    [FieldOffset(20)] public int Itn;
    [FieldOffset(24)] public int Diarize;
    [FieldOffset(32)] public IntPtr Language;
    [FieldOffset(40)] public IntPtr TargetLanguage;
    [FieldOffset(48)][MarshalAs(UnmanagedType.U1)] public bool KeepSpecialTags;
    [FieldOffset(56)] public IntPtr Family;
    [FieldOffset(64)] public int SpecKDrafts;
}

internal sealed record StatusEvent(string Evt, string State, string? Detail);
internal sealed record FinalEvent(string Evt, string Text);
internal sealed record ErrorEvent(string Evt, string Message);
