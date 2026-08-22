using System.Windows.Threading;
using FlowLocal.App;
using FlowLocal.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class HistoryControllerIntegrationTests
{
    [Fact]
    public Task SuccessfulReleasePersistsCompletedHistory() => RunStaAsync(async () =>
    {
        var history = new FakeHistory();
        var insertion = new FakeInsertion();
        using var controller = CreateController(history, insertion, new FakeAsr());

        await controller.HoldAsync();
        await controller.ReleaseAsync();

        var entry = Assert.Single(history.Entries);
        Assert.Equal(RecordingState.Completed, entry.State);
        Assert.Equal("raw transcript", entry.RawTranscript);
        Assert.Equal("cleaned transcript", entry.CleanedTranscript);
        Assert.Equal(TextInsertionMethod.Direct, entry.InsertionMethod);
        Assert.Equal(1, insertion.Calls);
        Assert.NotNull(entry.RecordingEndedAt);
        Assert.NotNull(entry.TotalDuration);
        Assert.Equal(1, history.RetentionPasses);
    });

    [Fact]
    public Task CancellationDeletesHistoryAndRecording() => RunStaAsync(async () =>
    {
        var history = new FakeHistory();
        using var controller = CreateController(history, new FakeInsertion(), new FakeAsr());
        await controller.HoldAsync();
        var entry = Assert.Single(history.Entries);
        Assert.True(File.Exists(entry.AudioFilePath));

        await controller.CancelAsync();

        Assert.Empty(history.Entries);
        Assert.False(File.Exists(entry.AudioFilePath));
    });

    [Fact]
    public Task CancellationDuringReleasePreventsInsertionAndDeletesSession() => RunStaAsync(async () =>
    {
        var history = new FakeHistory();
        var insertion = new FakeInsertion();
        var asr = new FakeAsr(blockCompletion: true);
        using var controller = CreateController(history, insertion, asr);
        await controller.HoldAsync();

        var release = controller.ReleaseAsync();
        await asr.CompletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = controller.CancelAsync();
        await Task.WhenAll(release, cancel).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, insertion.Calls);
        Assert.Empty(history.Entries);
    });

    [Fact]
    public Task FailedReleaseAppliesRetentionAfterTerminalSave() => RunStaAsync(async () =>
    {
        var history = new FakeHistory();
        using var controller = CreateController(history, new FakeInsertion(), new FakeAsr(failCompletion: true));

        await controller.HoldAsync();
        await controller.ReleaseAsync();

        Assert.Equal(RecordingState.Failed, Assert.Single(history.Entries).State);
        Assert.Equal(1, history.RetentionPasses);
    });

    private static DictationController CreateController(FakeHistory history, FakeInsertion insertion, FakeAsr asr) => new(
        new RecordingStateMachine(), new FakeTargets(), new FakeContextDetector(), new FakeStyleClassifier(),
        new FakeStyleStore(), new FakeAudio(), asr, new FakeCleaner(), new FakeBackend(), insertion,
        new OverlayWindow(), NullLogger<DictationController>.Instance, history);

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception exception) { error = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            if (error is null) completion.SetResult();
            else completion.SetException(error);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private sealed class FakeHistory : IHistoryRepository
    {
        private readonly Dictionary<Guid, HistoryEntry> _entries = [];
        public IReadOnlyCollection<HistoryEntry> Entries => _entries.Values;
        public int RetentionPasses { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateAsync(HistoryEntry entry, CancellationToken cancellationToken) { _entries.Add(entry.Id, entry); return Task.CompletedTask; }
        public Task UpdateAsync(HistoryEntry entry, CancellationToken cancellationToken) { _entries[entry.Id] = entry; return Task.CompletedTask; }
        public Task<HistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_entries.GetValueOrDefault(id));
        public Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistoryEntry>>(_entries.Values.ToList());
        public Task<IReadOnlyList<HistoryEntry>> GetRecoverableAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistoryEntry>>(_entries.Values.ToList());
        public Task DeleteAsync(Guid id, bool deleteAudio, CancellationToken cancellationToken)
        {
            if (deleteAudio && _entries.GetValueOrDefault(id)?.AudioFilePath is { } path) File.Delete(path);
            _entries.Remove(id);
            return Task.CompletedTask;
        }
        public Task DeleteAllAsync(bool deleteAudio, CancellationToken cancellationToken) { _entries.Clear(); return Task.CompletedTask; }
        public Task ClearRecordingsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<HistoryRetentionSettings> LoadRetentionSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(new HistoryRetentionSettings());
        public Task SaveRetentionSettingsAsync(HistoryRetentionSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken) { RetentionPasses++; return Task.CompletedTask; }
    }

    private sealed class FakeTargets : IActiveTargetTracker
    {
        public Task<ActiveTarget> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(_target);
        public Task<bool> RestoreAndValidateAsync(ActiveTarget target, CancellationToken cancellationToken) => Task.FromResult(true);
        private readonly ActiveTarget _target = new(Environment.ProcessId, 123, "test.exe", "Test", DateTimeOffset.UtcNow, FocusedChildWindowHandle: 456, IsInjectionSafe: true, FocusedAutomationId: "Editor", FocusedControlType: "50004");
    }

    private sealed class FakeInsertion : ITextInsertionService
    {
        public int Calls { get; private set; }
        public Task<TextInsertionResult> InsertAsync(ActiveTarget target, string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.Direct));
        }
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public Task StartAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAsr(bool blockCompletion = false, bool failCompletion = false) : IAsrService
    {
        public TaskCompletionSource CompletionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken)
        {
            CompletionStarted.TrySetResult();
            if (blockCompletion) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (failCompletion) throw new InvalidOperationException("ASR failed.");
            return new AsrResult("raw transcript");
        }
        public Task CancelSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCleaner : ITranscriptCleaner
    {
        public Task<CleanTranscriptResult> CleanAsync(RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken) => Task.FromResult(new CleanTranscriptResult("cleaned transcript"));
    }

    private sealed class FakeBackend : ICleanupBackend
    {
        public string BackendId => "fake";
        public string DisplayName => "Fake";
        public Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new BackendAvailability(true));
    }

    private sealed class FakeContextDetector : IApplicationContextDetector
    {
        public Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken) => Task.FromResult(new ApplicationContext(target.ExecutableName, target.ExecutableName, target.WindowTitle, target.FocusedControlType, false, null, null, new ContextDetectionDiagnostic(ContextDetectionConfidence.None, "test")));
    }

    private sealed class FakeStyleClassifier : IOutputStyleClassifier
    {
        public OutputClassification Classify(ApplicationContext context, OutputStyleSettings settings) => new(OutputContextCategory.General, TranscriptStyleResolver.Resolve(OutputContextCategory.General), ClassificationSource.General, "test", context.Detection);
    }

    private sealed class FakeStyleStore : IStyleOverrideStore
    {
        public Task<StyleOverrideLoadResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new StyleOverrideLoadResult(new OutputStyleSettings()));
        public Task SaveAsync(OutputStyleSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
