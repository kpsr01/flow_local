using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class CrashRecoveryService(IHistoryRepository history)
{
    public async Task<IReadOnlyList<HistoryEntry>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var recoverable = await history.GetRecoverableAsync(cancellationToken);
        var valid = new List<HistoryEntry>(recoverable.Count);
        foreach (var entry in recoverable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = entry.AudioFilePath is { } path
                ? PcmWaveFile.TryRepair(path)
                : WaveRepairResult.Invalid;
            if (result == WaveRepairResult.Repaired)
                valid.Add(entry);
            else if (result == WaveRepairResult.Invalid)
                await history.DeleteAsync(entry.Id, deleteAudio: true, cancellationToken);
        }

        return valid;
    }

    public Task DeleteAsync(HistoryEntry entry, CancellationToken cancellationToken = default) =>
        history.DeleteAsync(entry.Id, deleteAudio: true, cancellationToken);
}
