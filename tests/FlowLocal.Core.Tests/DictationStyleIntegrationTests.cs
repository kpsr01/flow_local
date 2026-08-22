using System.Windows.Threading;
using FlowLocal.App;
using FlowLocal.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class DictationStyleIntegrationTests
{
    [Fact]
    public Task DetectionFailure_ContinuesRecordingAndCleansWithGeneralStyle() => RunStaAsync(async () =>
    {
        var cleaner = new CapturingCleaner();
        using var fixture = new ControllerFixture(
            new ThrowingDetector(),
            new CapturingClassifier(),
            new MutableStore(new OutputStyleSettings()),
            cleaner);

        await fixture.Controller.HoldAsync();
        Assert.Equal(RecordingState.ListeningPushToTalk, fixture.State.State);
        Assert.Null(fixture.Controller.CurrentContext);
        Assert.Equal(OutputContextCategory.General, fixture.Controller.CurrentClassification!.Category);

        await fixture.Controller.ReleaseAsync();
        Assert.Equal(TranscriptStyleResolver.Resolve(OutputContextCategory.General), cleaner.Style);
        Assert.Equal(1, cleaner.Calls);
    });

    [Fact]
    public Task WebsiteDetectionDisabled_BypassesBrowserProbeAndRetainsApplicationClassification() => RunStaAsync(async () =>
    {
        var detector = new FakeDetector(Context(null));
        var classifier = new CapturingClassifier();
        using var fixture = new ControllerFixture(
            detector,
            classifier,
            new MutableStore(new OutputStyleSettings(WebsiteDetectionEnabled: false)),
            new CapturingCleaner());

        await fixture.Controller.HoldAsync();

        Assert.False(detector.DetectWebsite);
        Assert.NotNull(classifier.Context);
        Assert.True(fixture.Controller.CurrentContext!.IsBrowser);
        Assert.Equal("Google Chrome", fixture.Controller.CurrentContext.DisplayName);
        Assert.Null(fixture.Controller.CurrentContext.Domain);
        await fixture.Controller.CancelAsync();
    });

    [Fact]
    public Task RecordingStartStyle_ReachesCleanerUnchangedWhenSettingsLaterChange() => RunStaAsync(async () =>
    {
        var firstStyle = new TranscriptStyle("Session style", "Direct", "Bullets", EnableLists: true);
        var laterStyle = TranscriptStyleResolver.Resolve(OutputContextCategory.Email);
        var store = new MutableStore(new OutputStyleSettings(
            UniversalDefaultCategory: OutputContextCategory.Terminal,
            UniversalDefaultStyle: firstStyle));
        var classifier = new CapturingClassifier((_, settings) => new OutputClassification(
            settings.UniversalDefaultCategory,
            settings.UniversalDefaultStyle!,
            ClassificationSource.General,
            "test",
            new ContextDetectionDiagnostic(ContextDetectionConfidence.High, "test")));
        var cleaner = new CapturingCleaner();
        using var fixture = new ControllerFixture(new FakeDetector(Context(null)), classifier, store, cleaner);

        await fixture.Controller.HoldAsync();
        var classificationAtStart = fixture.Controller.CurrentClassification;
        store.Settings = new OutputStyleSettings(
            UniversalDefaultCategory: OutputContextCategory.Email,
            UniversalDefaultStyle: laterStyle);

        await fixture.Controller.ReleaseAsync();

        Assert.Same(firstStyle, cleaner.Style);
        Assert.Same(firstStyle, classificationAtStart!.Style);
        Assert.Equal(1, store.LoadCalls);
    });

    private static ApplicationContext Context(string? domain) => new(
        "chrome", "Google Chrome", "title", "Document", true, BrowserIdentity.Chrome,
        DomainNormalizer.TryNormalize(domain),
        new ContextDetectionDiagnostic(ContextDetectionConfidence.High, "test"));

    internal static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher;
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                });
                Dispatcher.Run();
                if (!completion.Task.IsCompleted)
                    completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private sealed class ControllerFixture : IDisposable
    {
        private readonly OverlayWindow overlay = new();
        public RecordingStateMachine State { get; } = new();
        public DictationController Controller { get; }

        public ControllerFixture(
            IApplicationContextDetector detector,
            IOutputStyleClassifier classifier,
            IStyleOverrideStore store,
            ITranscriptCleaner cleaner)
        {
            Controller = new DictationController(
                State,
                new FakeTargets(),
                detector,
                classifier,
                store,
                new FakeAudio(),
                new FakeAsr(),
                cleaner,
                new FakeBackend(),
                new FakeInsertion(),
                overlay,
                NullLogger<DictationController>.Instance);
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
            Environment.ProcessId, 123, "chrome", "title", DateTimeOffset.UtcNow,
            FocusedChildWindowHandle: 456, IsInjectionSafe: true, FocusedAutomationId: "Editor");
        public Task<ActiveTarget> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(Target);
        public Task<bool> RestoreAndValidateAsync(ActiveTarget target, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeDetector(ApplicationContext context) : IApplicationContextDetector
    {
        public bool? DetectWebsite { get; private set; }
        public Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken)
        {
            DetectWebsite = detectWebsite;
            return Task.FromResult(context);
        }
    }

    private sealed class ThrowingDetector : IApplicationContextDetector
    {
        public Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken) =>
            Task.FromException<ApplicationContext>(new InvalidOperationException("detection unavailable"));
    }

    private sealed class CapturingClassifier(
        Func<ApplicationContext, OutputStyleSettings, OutputClassification>? classify = null) : IOutputStyleClassifier
    {
        public ApplicationContext? Context { get; private set; }

        public OutputClassification Classify(ApplicationContext context, OutputStyleSettings settings)
        {
            Context = context;
            return classify?.Invoke(context, settings) ?? new OutputClassification(
                OutputContextCategory.General,
                TranscriptStyleResolver.Resolve(OutputContextCategory.General),
                ClassificationSource.General,
                "test",
                context.Detection);
        }
    }

    private sealed class MutableStore(OutputStyleSettings settings) : IStyleOverrideStore
    {
        public OutputStyleSettings Settings { get; set; } = settings;
        public int LoadCalls { get; private set; }
        public Task<StyleOverrideLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCalls++;
            return Task.FromResult(new StyleOverrideLoadResult(Settings));
        }
        public Task SaveAsync(OutputStyleSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            Settings = new OutputStyleSettings();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingCleaner : ITranscriptCleaner
    {
        public TranscriptStyle? Style { get; private set; }
        public int Calls { get; private set; }
        public Task<CleanTranscriptResult> CleanAsync(RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken)
        {
            Calls++;
            Style = style;
            return Task.FromResult(new CleanTranscriptResult(transcript.Text));
        }
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

    private sealed class FakeInsertion : ITextInsertionService
    {
        public Task<TextInsertionResult> InsertAsync(ActiveTarget target, string text, CancellationToken cancellationToken) =>
            Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.Direct));
    }
}
