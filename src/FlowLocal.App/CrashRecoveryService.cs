using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class CrashRecoveryService(IHistoryRepository history)
{
    public async Task<IReadOnlyList<HistoryEntry>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var recoverable = await history.GetRecoverableAsync(cancellationToken);
        var recovered = new List<HistoryEntry>(recoverable.Count);
        foreach (var entry in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = entry.AudioFilePath is { } path
                ? PcmWaveFile.TryRepair(path)
                : WaveRepairResult.Invalid;
            if (result == WaveRepairResult.Invalid &&
                string.IsNullOrWhiteSpace(entry.RawTranscript) &&
                string.IsNullOrWhiteSpace(entry.CleanedTranscript))
            {
                await history.DeleteAsync(entry.Id, deleteAudio: true, cancellationToken);
                continue;
            }

            var ended = entry.RecordingEndedAt ?? DateTimeOffset.UtcNow;
            var terminal = entry with
            {
                RecordingEndedAt = ended,
                Duration = entry.Duration ??
                    (entry.RecordingStartedAt is { } started ? ended - started : null),
                State = RecordingState.Failed,
                ErrorCode = DictationErrorCode.Interrupted
            };
            await history.UpdateAsync(terminal, cancellationToken);
            recovered.Add(terminal);
        }

        return recovered;
    }
}
