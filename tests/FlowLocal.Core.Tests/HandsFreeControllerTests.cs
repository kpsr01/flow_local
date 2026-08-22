using System.Windows.Threading;
using FlowLocal.App;
using FlowLocal.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class HandsFreeControllerTests
{
    [Fact]
    public Task DoubleTap_ConvertsToHandsFreeThenFinalizesOnRelease() => RunStaAsync(async () =>
    {
        using var fixture = new Fixture(handsFreeEnabled: true, intervalMilliseconds: 250);

        await fixture.Controller.HandleShortcutPressedAsync();
        Assert.Equal(RecordingState.ListeningPushToTalk, fixture.State.State);

        await fixture.Controller.HandleShortcutReleasedAsync();
        Assert.Equal(RecordingState.ListeningPushToTalk, fixture.State.State);

        await fixture.Controller.HandleShortcutPressedAsync();
        Assert.Equal(RecordingState.ListeningHandsFree, fixture.State.State);

        await fixture.Controller.ReleaseAsync();
        Assert.Equal(RecordingState.Idle, fixture.State.State);
        Assert.Equal(1, fixture.Insertion.Calls);
    });

    [Fact]
    public Task DeferralExpiry_FinalizesWithoutSecondTap() => RunStaAsync(async () =>
    {
        using var fixture = new Fixture(handsFreeEnabled: true, intervalMilliseconds: 150);

        await fixture.Controller.HandleShortcutPressedAsync();
        await fixture.Controller.HandleShortcutReleasedAsync();

        await WaitUntilAsync(() => fixture.State.State == RecordingState.Idle, TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Insertion.Calls);
    });

    [Fact]
    public Task Disabled_ReleaseFinalizesImmediately() => RunStaAsync(async () =>
    {
        using var fixture = new Fixture(handsFreeEnabled: false, intervalMilliseconds: 4000);

        await fixture.Controller.HandleShortcutPressedAsync();
        Assert.Equal(RecordingState.ListeningPushToTalk, fixture.State.State);

        await fixture.Controller.HandleShortcutReleasedAsync();
        Assert.Equal(RecordingState.Idle, fixture.State.State);
    });

    [Fact]
    public Task CancelDuringDeferralLeavesIdleWithoutInsertion() => RunStaAsync(async () =>
    {
        using var fixture = new Fixture(handsFreeEnabled: true, intervalMilliseconds: 5000);

        await fixture.Controller.HandleShortcutPressedAsync();
        await fixture.Controller.HandleShortcutReleasedAsync();
        await fixture.Controller.CancelAsync();

        Assert.Equal(RecordingState.Idle, fixture.State.State);
        Assert.Equal(0, fixture.Insertion.Calls);
        // The cancelled deferral must not resurrect the session.
        await Task.Delay(100);
        Assert.Equal(RecordingState.Idle, fixture.State.State);
    });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static Task RunStaAsync(Func<Task> action) => DictationStyleIntegrationTests.RunStaAsync(action);

    private sealed class Fixture : IDisposable
    {
        private readonly OverlayWindow overlay = new();
        public RecordingStateMachine State { get; } = new();
        public TrackingInsertion Insertion { get; } = new();
        public DictationController Controller { get; }

        public Fixture(bool handsFreeEnabled, int intervalMilliseconds)
        {
            Controller = new DictationController(
                State,
                new FakeTargets(),
                new FakeDetector(),
                new FakeClassifier(),
                new MutableStore(),
                new FakeAudio(),
                new FakeAsr(),
                new PassThroughCleaner(),
                new FakeBackend(),
                Insertion,
                overlay,
                NullLogger<DictationController>.Instance);
            Controller.HandsFreeEnabled = handsFreeEnabled;
            Controller.DoubleTapInterval = TimeSpan.FromMilliseconds(intervalMilliseconds);
        }

        public void Dispose()
        {
            Controller.Dispose();
            overlay.Close();
        }
    }

    private sealed class FakeTargets : IActiveTargetTracker
    {
        private static readonly ActiveTarget Target = new(
            Environment.ProcessId, 123, "notepad", "title", DateTimeOffset.UtcNow,
            FocusedChildWindowHandle: 456, IsInjectionSafe: true, FocusedAutomationId: "Editor");
        public Task<ActiveTarget> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(Target);
        public Task<bool> RestoreAndValidateAsync(ActiveTarget target, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeDetector : IApplicationContextDetector
    {
        public Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("detection unavailable");
    }

    private sealed class FakeClassifier : IOutputStyleClassifier
    {
        public OutputClassification Classify(ApplicationContext context, OutputStyleSettings settings) => new(
            OutputContextCategory.General,
            TranscriptStyleResolver.Resolve(OutputContextCategory.General),
            ClassificationSource.General,
            "test",
            context.Detection);
    }

    private sealed class MutableStore : IStyleOverrideStore
    {
        public OutputStyleSettings Settings { get; set; } = new();
        public Task<StyleOverrideLoadResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StyleOverrideLoadResult(Settings));
        public Task SaveAsync(OutputStyleSettings settings, CancellationToken cancellationToken) { Settings = settings; return Task.CompletedTask; }
        public Task ResetAsync(CancellationToken cancellationToken) { Settings = new OutputStyleSettings(); return Task.CompletedTask; }
    }

    private sealed class PassThroughCleaner : ITranscriptCleaner
    {
        public Task<CleanTranscriptResult> CleanAsync(RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken) =>
            Task.FromResult(new CleanTranscriptResult(transcript.Text));
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public Task StartAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAsr : IAsrService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken) => Task.FromResult(new AsrResult("raw transcript"));
        public Task CancelSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeBackend : ICleanupBackend
    {
        public string BackendId => "fake";
        public string DisplayName => "Fake";
        public Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new BackendAvailability(true));
    }

    private sealed class TrackingInsertion : ITextInsertionService
    {
        public int Calls { get; private set; }
        public Task<TextInsertionResult> InsertAsync(ActiveTarget target, string text, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.Direct));
        }
    }
}
