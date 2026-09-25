using System.Diagnostics;
using System.Text.Json;

const string AsrRevision = "6db4ce52ce242d3b5740af3274acbc0aedb0cb20";
const string DiarRevision = "a95331a1f347c66d68bc7e34d3eb05963bbb2f4c";
const string SpeakerRevision = "a9cb9321b07b4ee5b0ea47fdd25242d9cacd824a";
var root = Environment.GetEnvironmentVariable("FLOWLOCAL_MODELS_DIR") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLocal", "Models");
var assets = new (string File, string Url)[]
{
    ("encoder.int8.onnx", $"https://huggingface.co/smcleod/multitalker-parakeet-streaming-0.6b-v1-onnx-int8/resolve/{AsrRevision}/encoder.int8.onnx"),
    ("decoder_joint.int8.onnx", $"https://huggingface.co/smcleod/multitalker-parakeet-streaming-0.6b-v1-onnx-int8/resolve/{AsrRevision}/decoder_joint.int8.onnx"),
    ("tokenizer.model", $"https://huggingface.co/smcleod/multitalker-parakeet-streaming-0.6b-v1-onnx-int8/resolve/{AsrRevision}/tokenizer.model"),
    ("diar_streaming_sortformer_4spk-v2.1.onnx", $"https://huggingface.co/altunenes/parakeet-rs/resolve/{DiarRevision}/diar_streaming_sortformer_4spk-v2.1.onnx"),
    ("ecapa-speaker-v1.onnx", $"https://huggingface.co/vedk00/ecapa-voxceleb-speaker-embedding-onnx/resolve/{SpeakerRevision}/model/ecapa-speaker-v1.onnx"),
    ("fbank-80x201-f32.bin", $"https://huggingface.co/vedk00/ecapa-voxceleb-speaker-embedding-onnx/resolve/{SpeakerRevision}/model/fbank-80x201-f32.bin")
};
void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

async Task DownloadAsync()
{
    Directory.CreateDirectory(root);
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    foreach (var (file, url) in assets)
    {
        var path = Path.Combine(root, file);
        if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
        Emit(new { evt = "status", state = "Downloading", detail = file });
        var temp = path + "." + Environment.ProcessId + ".download";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(temp)) await response.Content.CopyToAsync(output);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

Process? native = null;
try
{
    while (await Console.In.ReadLineAsync() is { } command)
    {
        try
        {
            if (native is null || native.HasExited)
            {
                Emit(new { evt = "status", state = "Initializing" });
                await DownloadAsync();
                var executable = Path.Combine(AppContext.BaseDirectory, "flowlocal-asr-native.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("Native Multitalker worker not found", executable);
                native = Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Environment = { ["FLOWLOCAL_MODELS_DIR"] = root }
                }) ?? throw new InvalidOperationException("Native Multitalker worker could not start");
                _ = Task.Run(async () =>
                {
                    while (await native.StandardOutput.ReadLineAsync() is { } line) Console.WriteLine(line);
                });
                _ = native.StandardError.ReadToEndAsync();
            }
            await native.StandardInput.WriteLineAsync(command);
            await native.StandardInput.FlushAsync();
            if (JsonDocument.Parse(command).RootElement.GetProperty("cmd").GetString() == "exit") break;
        }
        catch (Exception exception) { Emit(new { evt = "error", message = exception.Message }); }
    }
}
finally { if (native is { HasExited: false }) native.Kill(entireProcessTree: true); native?.Dispose(); }
