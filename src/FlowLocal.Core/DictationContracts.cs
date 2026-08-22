namespace FlowLocal.Core;

public interface IAsrService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken);
    Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken);
    Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken);
    Task CancelSessionAsync(CancellationToken cancellationToken);
}

public interface ITranscriptCleaner
{
    Task<CleanTranscriptResult> CleanAsync(RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken);
}

public interface ICleanupBackend
{
    string BackendId { get; }
    string DisplayName { get; }
    Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken);
}

public interface IAudioCaptureService
{
    Task StartAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onAudio, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface ITextInsertionService
{
    Task<TextInsertionResult> InsertAsync(ActiveTarget target, string text, CancellationToken cancellationToken);
}

public interface IActiveTargetTracker
{
    Task<ActiveTarget> CaptureAsync(CancellationToken cancellationToken);
    Task<bool> RestoreAndValidateAsync(ActiveTarget target, CancellationToken cancellationToken);
}

public interface IApplicationContextDetector
{
    Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken);
}

public interface IOutputStyleClassifier
{
    OutputClassification Classify(ApplicationContext context, OutputStyleSettings settings);
}

public interface IStyleOverrideStore
{
    Task<StyleOverrideLoadResult> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(OutputStyleSettings settings, CancellationToken cancellationToken);
    Task ResetAsync(CancellationToken cancellationToken);
}

public interface IHistoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task CreateAsync(HistoryEntry entry, CancellationToken cancellationToken);
    Task UpdateAsync(HistoryEntry entry, CancellationToken cancellationToken);
    Task<HistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistoryEntry>> GetRecoverableAsync(CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, bool deleteAudio, CancellationToken cancellationToken);
    Task DeleteAllAsync(bool deleteAudio, CancellationToken cancellationToken);
    Task ClearRecordingsAsync(CancellationToken cancellationToken);
    Task<HistoryRetentionSettings> LoadRetentionSettingsAsync(CancellationToken cancellationToken);
    Task SaveRetentionSettingsAsync(HistoryRetentionSettings settings, CancellationToken cancellationToken);
    Task ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
