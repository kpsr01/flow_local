using FlowLocal.Core;
using System.IO;

namespace FlowLocal.App;

public static class AsrRetryService
{
    private const int ChunkSize = 32 * 1024;
    public static string CreateRecordingPath(Guid sessionId)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal",
            "Recordings");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{sessionId:N}.wav");
    }

    public static void DeleteRecording(string path) => File.Delete(path);


    public static async Task<AsrResult> RetryAsync(
        IAsrService asr,
        string recordingPath,
        AsrSessionOptions options,
        CancellationToken cancellationToken)
    {
        var retryOptions = options with { SessionId = Guid.NewGuid() };
        await asr.StartSessionAsync(retryOptions, cancellationToken);
        try
        {
            await using var stream = PcmWaveFile.OpenData(recordingPath);
            var buffer = new byte[ChunkSize];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await asr.PushAudioAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            return await asr.CompleteSessionAsync(cancellationToken);
        }
        catch
        {
            try { await asr.CancelSessionAsync(CancellationToken.None); } catch { }
            throw;
        }
    }
}
