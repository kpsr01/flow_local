using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Headless ASR worker: owns every Moonshine ONNX call so stalls or native aborts
// never take FlowLocal.App down. Protocol is line-delimited JSON on stdio.
//
// In:  {"cmd":"init"} | {"cmd":"start"} | {"cmd":"push","b64":...} | {"cmd":"complete"} | {"cmd":"cancel"} | {"cmd":"exit"}
// Out: {"evt":"status","state":"Initializing"|"Downloading"|"Loading"|"Ready","detail":...}
//      {"evt":"ok"} | {"evt":"final","text":...} | {"evt":"error","message":...}

// Constants pinned to the official medium streaming export
// (moonshine-ai/moonshine-streaming, onnx/medium/streaming_config.json).
const int SampleRate = 16_000;
const int ChunkSamples = 1280;   // 80 ms frontend chunk used by the reference implementation
// Encoder runs in a single final pass, so the streaming left-context/lookahead
// constants (total_lookahead=16, depth=14) are not needed here.
const int BosId = 1;
const int EosId = 2;
const string ModelDirName = "moonshine-streaming-medium";
const string ModelRepoUrl = "https://huggingface.co/moonshine-ai/moonshine-streaming/resolve/main/onnx/medium";
var ModelFiles = new[] { "frontend.onnx", "encoder.onnx", "adapter.onnx", "cross_kv.onnx", "decoder_kv.onnx", "tokenizer.json" };

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

Dictionary<string, InferenceSession>? sessions = null;
string[]? vocab = null;
List<float>? samples = null;

async Task EnsureInitializedAsync()
{
    if (sessions is not null) return;

    var dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FlowLocal", "Models", ModelDirName);
    Directory.CreateDirectory(dir);
    Status("Initializing");

    foreach (var file in ModelFiles)
    {
        var target = Path.Combine(dir, file);
        if (File.Exists(target)) continue;
        Status("Downloading", file);
        var temp = target + ".download";
        using (var response = await downloadClient.GetAsync($"{ModelRepoUrl}/{file}", HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var sink = File.Create(temp);
            await source.CopyToAsync(sink);
        }
        File.Move(temp, target);
    }

    Status("Loading");
    sessions = [];
    foreach (var file in ModelFiles)
    {
        if (!file.EndsWith(".onnx", StringComparison.Ordinal)) continue;
        // ponytail: one shared CPU SessionOptions; per-device tuning only if profiling demands it.
        sessions[file] = new InferenceSession(Path.Combine(dir, file), new SessionOptions());
    }

    vocab = LoadVocab(Path.Combine(dir, "tokenizer.json"));
    Status("Ready", ModelDirName);
}

static string[] LoadVocab(string tokenizerPath)
{
    using var document = JsonDocument.Parse(File.ReadAllText(tokenizerPath));
    var root = document.RootElement;
    var vocabElement = root.GetProperty("model").GetProperty("vocab");
    var result = new string[vocabElement.EnumerateObject().Count()];
    foreach (var property in vocabElement.EnumerateObject())
    {
        var id = property.Value.GetInt32();
        if (id >= 0 && id < result.Length) result[id] = property.Name;
    }
    if (root.TryGetProperty("added_tokens", out var added))
    {
        foreach (var token in added.EnumerateArray())
        {
            var id = token.GetProperty("id").GetInt32();
            var content = token.GetProperty("content").GetString();
            if (content is not null && id >= 0 && id < result.Length) result[id] = content;
        }
    }
    return result;
}

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

        var text = Transcribe(audio).Trim();
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
        samples = null;
    }
}

void CancelSession()
{
    samples = null;
    Emit("{\"evt\":\"ok\"}");
}

string Transcribe(float[] audio)
{
    // Frontend: fixed 1280-sample chunks with carried state; trailing remainder (< 80 ms)
    // is dropped exactly as in the reference C++ implementation.
    var features = new List<float>();
    var featureRows = 0;
    float[] sampleBuffer = new float[79];
    long sampleLen = 0;
    float[] conv1 = new float[4 * 768];
    float[] conv2 = new float[4 * 1536];
    long frameCount = 0;

    for (var offset = 0; offset + ChunkSamples <= audio.Length; offset += ChunkSamples)
    {
        var chunk = new DenseTensor<float>(new float[ChunkSamples], new[] { 1, ChunkSamples });
        for (var i = 0; i < ChunkSamples; i++) chunk[0, i] = audio[offset + i];
        using var results = sessions!["frontend.onnx"].Run([
            NamedOnnxValue.CreateFromTensor("audio_chunk", chunk),
            NamedOnnxValue.CreateFromTensor("sample_buffer", new DenseTensor<float>(sampleBuffer, new[] { 1, 79 })),
            NamedOnnxValue.CreateFromTensor("sample_len", new DenseTensor<long>(new[] { sampleLen }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("conv1_buffer", new DenseTensor<float>(conv1, new[] { 1, 768, 4 })),
            NamedOnnxValue.CreateFromTensor("conv2_buffer", new DenseTensor<float>(conv2, new[] { 1, 1536, 4 })),
            NamedOnnxValue.CreateFromTensor("frame_count", new DenseTensor<long>(new[] { frameCount }, new[] { 1 })),
        ]);
        var rows = results.First(r => r.Name == "features")!.AsTensor<float>().ToDenseTensor();
        var rowCount = checked((int)rows.Dimensions[1]);
        foreach (var value in rows.Buffer.Span) features.Add(value);
        featureRows += rowCount;
        sampleBuffer = results.First(r => r.Name == "sample_buffer_out")!.AsTensor<float>().ToArray();
        sampleLen = results.First(r => r.Name == "sample_len_out")!.AsTensor<long>()[0];
        conv1 = results.First(r => r.Name == "conv1_buffer_out")!.AsTensor<float>().ToArray();
        conv2 = results.First(r => r.Name == "conv2_buffer_out")!.AsTensor<float>().ToArray();
        frameCount = results.First(r => r.Name == "frame_count_out")!.AsTensor<long>()[0];
    }

    if (featureRows == 0) return string.Empty;

    // Encoder runs once over all stable frames (is_final drops the lookahead window).
    var featureTensor = new DenseTensor<float>(features.ToArray(), [1, featureRows, 768]);
    float[] encoded;
    using (var result = sessions!["encoder.onnx"].Run([NamedOnnxValue.CreateFromTensor("features", featureTensor)]))
    {
        encoded = result.First(r => r.Name == "encoded")!.AsTensor<float>().ToArray();
    }

    float[] memory;
    using (var result = sessions!["adapter.onnx"].Run([
        NamedOnnxValue.CreateFromTensor("encoded", new DenseTensor<float>(encoded, [1, featureRows, 768])),
        NamedOnnxValue.CreateFromTensor("pos_offset", new DenseTensor<long>(new long[] { 0 }, new[] { 1 })),
    ]))
    {
        memory = result.First(r => r.Name == "memory")!.AsTensor<float>().ToArray();
    }

    var memoryDims = new[] { 1, memory.Length / 640, 640 };
    float[] kCross, vCross;
    using (var result = sessions!["cross_kv.onnx"].Run([
        NamedOnnxValue.CreateFromTensor("memory", new DenseTensor<float>(memory, memoryDims)),
    ]))
    {
        kCross = result.First(r => r.Name == "k_cross")!.AsTensor<float>().ToArray();
        vCross = result.First(r => r.Name == "v_cross")!.AsTensor<float>().ToArray();
    }

    var crossLen = memoryDims[1];
    var crossDims = new[] { 14, 1, 10, crossLen, 64 };

    // Greedy autoregressive decode from BOS through decoder_kv with a growing self cache.
    var ids = new List<int>();
    float[]? kSelf = null, vSelf = null;
    var selfLen = 0;
    var selfDimsTemplate = new[] { 14, 1, 10, 0, 64 }; // [depth, batch, heads, cache_len, head_dim]
    var maxTokens = Math.Clamp((int)Math.Ceiling(audio.Length / (double)SampleRate * 6.5), 1, 256);
    var next = BosId;
    while (ids.Count < maxTokens)
    {
        var token = new DenseTensor<long>(new long[] { next }, new[] { 1, 1 });
        using var result = sessions!["decoder_kv.onnx"].Run([
            NamedOnnxValue.CreateFromTensor("token", token),
            NamedOnnxValue.CreateFromTensor("k_self", new DenseTensor<float>(kSelf ?? [], WithCacheLen(selfDimsTemplate, selfLen))),
            NamedOnnxValue.CreateFromTensor("v_self", new DenseTensor<float>(vSelf ?? [], WithCacheLen(selfDimsTemplate, selfLen))),
            NamedOnnxValue.CreateFromTensor("out_k_cross", new DenseTensor<float>(kCross, crossDims)),
            NamedOnnxValue.CreateFromTensor("out_v_cross", new DenseTensor<float>(vCross, crossDims)),
        ]);
        var logits = result.First(r => r.Name == "logits")!.AsTensor<float>().ToDenseTensor();
        next = Argmax(logits.Buffer.Span[^32768..]);
        if (next == EosId) break;
        ids.Add(next);
        kSelf = result.First(r => r.Name == "out_k_self")!.AsTensor<float>().ToArray();
        vSelf = result.First(r => r.Name == "out_v_self")!.AsTensor<float>().ToArray();
        selfLen++;
    }

    return Detokenize(ids, vocab!);
}

static int[] WithCacheLen(int[] dims, int cacheLen)
{
    var copy = (int[])dims.Clone();
    copy[3] = cacheLen;
    return copy;
}

static int Argmax(ReadOnlySpan<float> values)
{
    var best = 0;
    for (var i = 1; i < values.Length; i++)
    {
        if (values[i] > values[best]) best = i;
    }
    return best;
}

static string Detokenize(List<int> ids, string[] vocab)
{
    var pendingBytes = new List<byte>();
    var text = new StringBuilder();
    void FlushBytes()
    {
        if (pendingBytes.Count == 0) return;
        text.Append(Encoding.UTF8.GetString(pendingBytes.ToArray()));
        pendingBytes.Clear();
    }

    foreach (var id in ids)
    {
        if (id <= 0 || id >= vocab.Length || vocab[id] is not { } token) continue;
        // Byte-fallback tokens "<0xHH>" carry raw bytes; other "<...>" tokens are special.
        if (token.Length >= 3 && token[0] == '<' && token[^1] == '>')
        {
            if (token.Length == 6 && token[1] == '0' && token[2] == 'x'
                && Uri.IsHexDigit(token[3]) && Uri.IsHexDigit(token[4]))
            {
                pendingBytes.Add(Convert.ToByte(token[3..5].ToString(), 16));
            }
            continue;
        }
        FlushBytes();
        text.Append(token.Replace('\u2581', ' '));
    }
    FlushBytes();
    return text.ToString().Trim();
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

internal sealed record StatusEvent(string Evt, string State, string? Detail);
internal sealed record FinalEvent(string Evt, string Text);
internal sealed record ErrorEvent(string Evt, string Message);
