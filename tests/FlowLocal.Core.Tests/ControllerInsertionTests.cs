using System.Windows.Threading;
using FlowLocal.App;
using Microsoft.Extensions.Logging.Abstractions;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class ControllerInsertionTests
{
    public static TheoryData<Scenario> Scenarios => new()
    {
        new("successful insertion", true, SafeTarget(), new(true, TextInsertionMethod.Direct), "Completed", 1),
        new("target disappeared", false, SafeTarget(), new(true, TextInsertionMethod.Direct), "No text target", 0),
        new("elevated target", true, SafeTarget() with { IsInjectionSafe = false }, new(false, TextInsertionMethod.SendInput), "Input blocked (elevated or protected target)", 1),
        new("clipboard recovery", true, SafeTarget(), new(false, TextInsertionMethod.ClipboardOnly), "Copied to clipboard — paste manually", 1),
        new("unsafe textless target", true, SafeTarget() with { IsInjectionSafe = false, FocusedAutomationId = null, FocusedControlType = null, FocusedChildWindowHandle = 0 }, new(false, TextInsertionMethod.Direct), "No text target", 1)
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task Release_InsertsOnceOrShowsRecovery(Scenario scenario) => RunStaAsync(async () =>
    {
        var state = new RecordingStateMachine();
        var targets = new FakeTargets(scenario.Target, scenario.RestoreSucceeds);
        var insertion = new FakeInsertion(scenario.Result);
        var overlay = new OverlayWindow();
        var controller = new DictationController(
            state,
            targets,
            new FakeContextDetector(),
            new FakeStyleClassifier(),
            new FakeStyleStore(),
            new FakeAudio(),
            new FakeAsr(),
            new FakeCleaner(),
            new FakeBackend(),
            insertion,
            overlay,
            NullLogger<DictationController>.Instance);
        try
        {
            await controller.HoldAsync();
            await controller.ReleaseAsync();

            Assert.Equal(1, targets.CaptureCalls);
            Assert.Equal(1, targets.RestoreCalls);
            Assert.Equal(scenario.ExpectedInsertCalls, insertion.Calls);
            Assert.Equal(scenario.ExpectedStatus, overlay.StatusText.Text);
            if (scenario.ExpectedInsertCalls == 1)
            {
                Assert.Same(scenario.Target, insertion.Target);
                Assert.Equal("cleaned transcript", insertion.Text);
            }
        }
        finally
        {
            controller.Dispose();
            overlay.Close();
        }
    });

    private static ActiveTarget SafeTarget() => new(
        Environment.ProcessId,
        123,
        "test",
        "owned target",
        DateTimeOffset.UtcNow,
        FocusedChildWindowHandle: 456,
        IsInjectionSafe: true,
        FocusedAutomationId: "Editor",
        FocusedControlType: "50004");

    private static Task RunStaAsync(Func<Task> action)
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

    public sealed record Scenario(
        string Name,
        bool RestoreSucceeds,
        ActiveTarget Target,
        TextInsertionResult Result,
        string ExpectedStatus,
        int ExpectedInsertCalls)
    {
        public override string ToString() => Name;
    }

    private sealed class FakeTargets(ActiveTarget target, bool restoreSucceeds) : IActiveTargetTracker
    {
        public int CaptureCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public Task<ActiveTarget> CaptureAsync(CancellationToken cancellationToken)
        {
            CaptureCalls++;
            return Task.FromResult(target);
        }

        public Task<bool> RestoreAndValidateAsync(ActiveTarget captured, CancellationToken cancellationToken)
        {
            RestoreCalls++;
            Assert.Same(target, captured);
            return Task.FromResult(restoreSucceeds);
        }
    }

    private sealed class FakeInsertion(TextInsertionResult result) : ITextInsertionService
    {
        public int Calls { get; private set; }
        public ActiveTarget? Target { get; private set; }
        public string? Text { get; private set; }

        public Task<TextInsertionResult> InsertAsync(ActiveTarget target, string text, CancellationToken cancellationToken)
        {
            Calls++;
            Target = target;
            Text = text;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public Task StartAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeContextDetector : IApplicationContextDetector
    {
        public Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationContext(
                target.ExecutableName,
                target.ExecutableName,
                target.WindowTitle,
                target.FocusedControlType,
                false,
                null,
                null,
                new ContextDetectionDiagnostic(ContextDetectionConfidence.None, "test")));
    }

    private sealed class FakeStyleClassifier : IOutputStyleClassifier
    {
        public OutputClassification Classify(ApplicationContext context, OutputStyleSettings settings) =>
            new(
                OutputContextCategory.General,
                TranscriptStyleResolver.Resolve(OutputContextCategory.General),
                ClassificationSource.General,
                "test",
                context.Detection);
    }

    private sealed class FakeStyleStore : IStyleOverrideStore
    {
        public Task<StyleOverrideLoadResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StyleOverrideLoadResult(new OutputStyleSettings()));

        public Task SaveAsync(OutputStyleSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeAsr : IAsrService
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken) => Task.FromResult(new AsrResult("raw transcript"));
        public Task CancelSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCleaner : ITranscriptCleaner
    {
        public Task<CleanTranscriptResult> CleanAsync(RawTranscript transcript, TranscriptStyle style, CancellationToken cancellationToken) =>
            Task.FromResult(new CleanTranscriptResult("cleaned transcript"));
    }

    private sealed class FakeBackend : ICleanupBackend
    {
        public string BackendId => "fake";
        public string DisplayName => "Fake";
        public Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken) => Task.FromResult(new BackendAvailability(true));
    }
}
