using System.Text;
using System.Text.Json;
using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

// Headless ASR worker: owns every native Foundry Local call so stalls or native
// aborts never take FlowLocal.App down. Protocol is line-delimited JSON on stdio.
//
// In:  {"cmd":"init"} | {"cmd":"start"} | {"cmd":"push","b64":...} | {"cmd":"complete"} | {"cmd":"cancel"} | {"cmd":"exit"}
// Out: {"evt":"status","state":"Initializing"|"Downloading"|"Loading"|"Ready","detail":...}
//      {"evt":"ok"} | {"evt":"final","text":...} | {"evt":"error","message":...}

const string ModelAlias = "nemotron-speech-streaming-en-0.6b";

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var stdout = Console.Out;
var outputLock = new object();

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

OpenAIAudioClient? audioClient = null;
LiveAudioTranscriptionSession? session = null;
Task? streamTask = null;
StringBuilder? transcript = null;

async Task EnsureInitializedAsync()
{
    if (audioClient is not null) return;

    Status("Initializing");
    var sharedCache = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "cache", "models");
    var configuration = Directory.Exists(sharedCache)
        ? new Configuration { AppName = "FlowLocalAsrWorker", LogLevel = LogLevel.Information, ModelCacheDir = sharedCache }
        : new Configuration { AppName = "FlowLocalAsrWorker", LogLevel = LogLevel.Information };

    await FoundryLocalManager.CreateAsync(configuration, NullLogger.Instance);
    var manager = FoundryLocalManager.Instance;
    await manager.DownloadAndRegisterEpsAsync();
    var catalog = await manager.GetCatalogAsync();
    var model = await catalog.GetModelAsync(ModelAlias)
        ?? throw new InvalidOperationException($"Foundry Local model '{ModelAlias}' is unavailable.");

    // The CUDA build of the streaming ASR model aborts natively mid-session on some
    // consumer GPUs; prefer any non-CUDA variant (CPU/OpenVINO/WebGPU) when present.
    var nonCuda = model.Variants.FirstOrDefault(v =>
        !string.Equals(v.Info?.Runtime?.ExecutionProvider, "CUDAExecutionProvider", StringComparison.OrdinalIgnoreCase));
    if (nonCuda is not null && !string.Equals(nonCuda.Id, model.Id, StringComparison.Ordinal))
    {
        Status("Selecting", nonCuda.Id);
        model.SelectVariant(nonCuda);
    }

    if (!await model.IsCachedAsync())
    {
        Status("Downloading", model.Id);
        await model.DownloadAsync();
    }

    Status("Loading", model.Info.Runtime?.ExecutionProvider);
    if (!await model.IsLoadedAsync()) await model.LoadAsync();
    audioClient = await model.GetAudioClientAsync();
    Status("Ready", model.Id);
}

async Task StartSessionAsync()
{
    if (session is not null) return;
    await EnsureInitializedAsync();
    var fresh = audioClient!.CreateLiveTranscriptionSession();
    fresh.Settings.SampleRate = 16_000;
    fresh.Settings.BitsPerSample = 16;
    fresh.Settings.Channels = 1;
    fresh.Settings.Language = "en";
    await fresh.StartAsync();
    session = fresh;
    transcript = new StringBuilder();
    streamTask = ConsumeAsync(fresh, transcript);
    Emit("{\"evt\":\"ok\"}");
}

async Task PushAsync(string base64)
{
    if (session is null) throw new InvalidOperationException("No ASR session is active.");
    await session.AppendAsync(Convert.FromBase64String(base64));
}

async Task CompleteSessionAsync()
{
    if (session is null) throw new InvalidOperationException("No ASR session is active.");
    var target = session;
    var task = streamTask!;
    var buffer = transcript!;
    try
    {
        await target.StopAsync();
        await task;
        var text = buffer.ToString().Trim();
        if (text.Length == 0)
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
        session = null; streamTask = null; transcript = null;
        await target.DisposeAsync();
    }
}

async Task CancelSessionAsync()
{
    var target = session;
    session = null; streamTask = null; transcript = null;
    if (target is not null) await target.DisposeAsync();
    Emit("{\"evt\":\"ok\"}");
}

static async Task ConsumeAsync(LiveAudioTranscriptionSession source, StringBuilder sink)
{
    string? pending = null;
    await foreach (var result in source.GetStream())
    {
        var text = result.Content?.FirstOrDefault()?.Text;
        if (string.IsNullOrEmpty(text)) continue;
        if (result.IsFinal)
        {
            if (pending is not null && !text.StartsWith(pending, StringComparison.Ordinal))
                AppendWithoutDuplicate(sink, pending);
            AppendWithoutDuplicate(sink, text);
            pending = null;
        }
        else
        {
            pending = text;
        }
    }
    if (pending is not null) AppendWithoutDuplicate(sink, pending);
}

static void AppendWithoutDuplicate(StringBuilder sink, string text)
{
    var committed = sink.ToString();
    var overlap = Math.Min(committed.Length, text.Length);
    while (overlap > 0 && !committed.AsSpan(committed.Length - overlap).SequenceEqual(text.AsSpan(0, overlap)))
        overlap--;
    sink.Append(text.AsSpan(overlap));
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
                    await PushAsync(document.RootElement.GetProperty("b64").GetString()!);
                    break;
                case "complete":
                    await CompleteSessionAsync();
                    break;
                case "cancel":
                    await CancelSessionAsync();
                    break;
                case "exit":
                    return;
            }
        }
        catch (Exception exception)
        {
            var message = exception.Message.Length > 500 ? exception.Message[..500] : exception.Message;
            Emit(JsonSerializer.Serialize(new ErrorEvent("error", message), jsonOptions));
            if (command is "push" or "start") { session = null; streamTask = null; transcript = null; }
        }
    }
}

internal sealed record StatusEvent(string Evt, string State, string? Detail);
internal sealed record FinalEvent(string Evt, string Text);
internal sealed record ErrorEvent(string Evt, string Message);
