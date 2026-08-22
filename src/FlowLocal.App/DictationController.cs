using System.Diagnostics;
using FlowLocal.Core;
using Microsoft.Extensions.Logging;

namespace FlowLocal.App;

public sealed class DictationController : IDisposable
{
    private static readonly TranscriptStyle DefaultStyle = TranscriptStyleResolver.Resolve(OutputContextCategory.General);
    private readonly RecordingStateMachine _stateMachine;
    private readonly IActiveTargetTracker _targets;
    private readonly IApplicationContextDetector _contextDetector;
    private readonly IOutputStyleClassifier _styleClassifier;
    private readonly IStyleOverrideStore _styleOverrides;
    private readonly IAudioCaptureService _audio;
    private readonly IAsrService _asr;
    private readonly ITranscriptCleaner _cleaner;
    private readonly ICleanupBackend _cleanupBackend;
    private readonly ITextInsertionService _insertion;
    private readonly OverlayWindow _overlay;
    private readonly IHistoryRepository? _history;
    private readonly ILogger<DictationController> _logger;
    private readonly string _asrModelName;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sessionCancellationLock = new();
    private CancellationTokenSource? _sessionCancellation;
    private PcmWaveFile? _recording;
    private string? _recordingPath;
    private AsrSessionOptions? _sessionOptions;
    private Exception? _asrFailure;
    private ActiveTarget? _target;
    private TranscriptStyle _style = DefaultStyle;
    private HistoryEntry? _entry;
    private long _started;
    private DateTimeOffset _listeningStartedAt;
    private DoubleTapDetector _doubleTap;
    private CancellationTokenSource? _releaseDeferral;
    private bool _disposed;

    public ApplicationContext? CurrentContext { get; private set; }
    public OutputClassification? CurrentClassification { get; private set; }

    /// <summary>Enables hands-free activation by double-tapping the push-to-talk chord.</summary>
    public bool HandsFreeEnabled
    {
        get => _doubleTap.Enabled;
        set => _doubleTap.Enabled = value;
    }

    /// <summary>Maximum gap between the two taps of a hands-free activation.</summary>
    public TimeSpan DoubleTapInterval
    {
        get => _doubleTap.DoubleTapInterval;
        set => _doubleTap = new DoubleTapDetector(_doubleTap.Enabled, value);
    }

    public DictationController(
        RecordingStateMachine stateMachine, IActiveTargetTracker targets,
        IApplicationContextDetector contextDetector, IOutputStyleClassifier styleClassifier,
        IStyleOverrideStore styleOverrides, IAudioCaptureService audio, IAsrService asr,
        ITranscriptCleaner cleaner, ICleanupBackend cleanupBackend,
        ITextInsertionService insertion, OverlayWindow overlay, ILogger<DictationController> logger,
        IHistoryRepository? history = null, string? asrModelName = null)
    {
        _stateMachine = stateMachine;
        _targets = targets;
        _contextDetector = contextDetector;
        _styleClassifier = styleClassifier;
        _styleOverrides = styleOverrides;
        _audio = audio;
        _asr = asr;
        _cleaner = cleaner;
        _cleanupBackend = cleanupBackend;
        _insertion = insertion;
        _overlay = overlay;
        _history = history;
        _logger = logger;
        _asrModelName = string.IsNullOrWhiteSpace(asrModelName) ? "unknown" : asrModelName.Trim();
        _doubleTap = new DoubleTapDetector(false, TimeSpan.FromMilliseconds(400));
    }

    /// <summary>Routes a shortcut key-down through double-tap detection.</summary>
    public Task HandleShortcutPressedAsync()
    {
        var decision = _doubleTap.OnPressed(DateTimeOffset.UtcNow);
        return decision switch
        {
            DoubleTapDecision.StartPushToTalk => HoldAsync(),
            DoubleTapDecision.ConvertToHandsFree => ConvertToHandsFreeAsync(),
            _ => Task.CompletedTask
        };
    }

    /// <summary>Routes a shortcut key-up through double-tap detection.</summary>
    public async Task HandleShortcutReleasedAsync()
    {
        var decision = _doubleTap.OnReleased(DateTimeOffset.UtcNow);
        if (decision == DoubleTapDecision.DeferRelease)
        {
            // Do not await: the deferral must not block the shortcut handler.
            _ = BeginReleaseDeferralAsync();
        }
        else if (decision == DoubleTapDecision.Finalize)
        {
            await ReleaseAsync();
        }
    }

    private async Task BeginReleaseDeferralAsync()
    {
        var deferral = new CancellationTokenSource();
        _releaseDeferral?.Dispose();
        _releaseDeferral = deferral;
        try
        {
            await Task.Delay(_doubleTap.DoubleTapInterval, deferral.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_doubleTap.OnDeferralExpired() == DoubleTapDecision.Finalize)
        {
            await ReleaseAsync();
        }
    }

    private void AbortReleaseDeferral()
    {
        try { _releaseDeferral?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task ConvertToHandsFreeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_stateMachine.State != RecordingState.ListeningPushToTalk) return;
            AbortReleaseDeferral();
            _stateMachine.ConvertToHandsFree();
            await SaveAsync(_entry! with { State = RecordingState.ListeningHandsFree }, CancellationToken.None);
            LogLifecycle(_entry!);
            await _overlay.Dispatcher.InvokeAsync(
                () => _overlay.ShowHandsFree(_listeningStartedAt));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _asr.InitializeAsync(cancellationToken);
        var availability = await _cleanupBackend.CheckAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
            throw new InvalidOperationException(availability.UnavailableReason ?? "Transcript cleanup is unavailable.");
    }

    public async Task HoldAsync()
    {
        var token = CancellationToken.None;
        await _lifecycle.WaitAsync();
        try
        {
            if (_stateMachine.State != RecordingState.Idle) return;
            ReplaceSessionCancellation();
            token = GetSessionCancellationToken();
            _stateMachine.Start(RecordingMode.PushToTalk, token);
            try { _target = await _targets.CaptureAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { _target = null; }

            CurrentContext = null;
            CurrentClassification = GeneralClassification();
            _style = CurrentClassification.Style;
            if (_target is not null)
            {
                try
                {
                    var settings = (await _styleOverrides.LoadAsync(token)).Settings;
                    var context = await _contextDetector.DetectAsync(_target, settings.WebsiteDetectionEnabled, token);
                    CurrentContext = context;
                    CurrentClassification = _styleClassifier.Classify(context, settings);
                    _style = CurrentClassification.Style;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { }
            }

            _overlay.ShowReady();
            _overlay.ShowOverlay();
            var now = DateTimeOffset.UtcNow;
            var sessionId = Guid.NewGuid();
            var recordingPath = AsrRetryService.CreateRecordingPath(sessionId);
            _recordingPath = recordingPath;
            _sessionOptions = new AsrSessionOptions(sessionId);
            _asrFailure = null;
            _started = Stopwatch.GetTimestamp();
            _entry = new HistoryEntry(
                sessionId, now, now, null, null, null, null, recordingPath,
                CurrentContext?.DisplayName ?? _target?.ExecutableName,
                CurrentContext?.ExecutableName ?? _target?.ExecutableName,
                CurrentContext?.Domain, CurrentClassification?.Category, _style,
                _asrModelName, _cleanupBackend.DisplayName, null, null, null, null, null,
                RecordingState.Starting);            if (_history is not null) await _history.CreateAsync(_entry, CancellationToken.None);
            LogLifecycle(_entry);

            // The recoverable row must exist before the file is opened.
            _recording = new PcmWaveFile(recordingPath);
            await _asr.StartSessionAsync(_sessionOptions, token);
            await _audio.StartAsync(async (chunk, ct) =>
            {
                await _recording.WriteAsync(chunk, ct);
                if (_asrFailure is null)
                    try { await _asr.PushAudioAsync(chunk, ct); } catch (Exception ex) { _asrFailure = ex; }
            }, token);
            _stateMachine.BeginListening();
            _listeningStartedAt = _entry!.RecordingStartedAt ?? DateTimeOffset.UtcNow;
            await SaveAsync(_entry with { State = _stateMachine.State }, token);
            LogLifecycle(_entry);
            await _overlay.Dispatcher.InvokeAsync(() => _overlay.ShowListening(_listeningStartedAt));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DeleteSessionAsync();
            if (_stateMachine.State != RecordingState.Cancelled) _stateMachine.Cancel();
            Reset();
        }
        catch (Exception ex) { await FailAsync(ex); }
        finally { _lifecycle.Release(); }
    }

    public async Task ReleaseAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_stateMachine.State is not (RecordingState.ListeningPushToTalk or RecordingState.ListeningHandsFree)) return;
            AbortReleaseDeferral();
            var token = GetSessionCancellationToken();
            _stateMachine.TransitionTo(RecordingState.Stopping);
            try
            {
                await _audio.StopAsync(token).WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // Capture delivery is wedged; the finalized WAV still holds everything recorded.
            }
            CloseRecording();
            var ended = DateTimeOffset.UtcNow;
            await SaveAsync(_entry! with { RecordingEndedAt = ended, Duration = ended - _entry!.RecordingStartedAt, State = RecordingState.Stopping }, token);
            LogLifecycle(_entry);

            _stateMachine.TransitionTo(RecordingState.Transcribing);
            await _overlay.Dispatcher.InvokeAsync(_overlay.ShowTranscribing);
            var step = Stopwatch.GetTimestamp();
            AsrResult transcription;
            var retries = _entry!.RetryCount;
            try
            {
                if (_asrFailure is not null) throw _asrFailure;
                transcription = await _asr.CompleteSessionAsync(token);
            }
            catch (Exception original)
            {
                try
                {
                    await _asr.CancelSessionAsync(token);
                    retries++;
                    transcription = await AsrRetryService.RetryAsync(_asr, _recordingPath!, _sessionOptions!, token);
                }
                catch { throw original; }
            }
            var raw = new RawTranscript(transcription.Text);
            if (string.IsNullOrWhiteSpace(raw.Text)) throw new InvalidOperationException("Speech recognition returned an empty transcript.");
            await SaveAsync(_entry with { RawTranscript = raw.Text, AsrDuration = Stopwatch.GetElapsedTime(step), RetryCount = retries, State = RecordingState.Transcribing }, token);
            LogLifecycle(_entry);

            _stateMachine.TransitionTo(RecordingState.Cleaning);
            await _overlay.Dispatcher.InvokeAsync(_overlay.ShowCleaning);
            step = Stopwatch.GetTimestamp();
            var (cleaned, usedFallback) = await CleanWithFallbackStatusAsync(_cleaner, raw, _style, token);
            await SaveAsync(_entry with
            {
                CleanedTranscript = cleaned.Text,
                CleanupDuration = Stopwatch.GetElapsedTime(step),
                State = RecordingState.Cleaning,
                ErrorCode = usedFallback ? DictationErrorCode.CleanupFailed : DictationErrorCode.None
            }, token);
            LogLifecycle(_entry);

            if (_target is null || !await _targets.RestoreAndValidateAsync(_target, token))
            {
                await FailInsertionAsync(_overlay.ShowNoTextTarget, DictationErrorCode.TargetUnavailable);
                return;
            }
            token.ThrowIfCancellationRequested();
            _stateMachine.TransitionTo(RecordingState.Inserting);
            await _overlay.Dispatcher.InvokeAsync(_overlay.ShowInserting);
            step = Stopwatch.GetTimestamp();
            var result = await _insertion.InsertAsync(_target, cleaned.Text, token);
            await SaveAsync(_entry with { InsertionDuration = Stopwatch.GetElapsedTime(step), InsertionMethod = result.Method, State = RecordingState.Inserting }, token);
            LogLifecycle(_entry);
            if (!result.Succeeded || result.Method == TextInsertionMethod.ClipboardOnly)
            {
                await FailInsertionAsync(result.Method == TextInsertionMethod.ClipboardOnly
                    ? _overlay.ShowCopiedToClipboard
                    : !HasTextTarget(_target) ? _overlay.ShowNoTextTarget
                    : _target.IsInjectionSafe ? _overlay.ShowInsertionFailed : _overlay.ShowInputBlocked);
                return;
            }

            _stateMachine.TransitionTo(RecordingState.Completed);
            await SaveAsync(_entry with { State = RecordingState.Completed, TotalDuration = Stopwatch.GetElapsedTime(_started) }, token);
            if (_history is not null) await _history.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            LogLifecycle(_entry);
            await _overlay.Dispatcher.InvokeAsync(_overlay.ShowCompleted);
            Reset(hideOverlay: false);
        }
        catch (OperationCanceledException) when (GetSessionCancellationToken().IsCancellationRequested) { }
        catch (Exception ex) { await FailAsync(ex); }
        finally { _lifecycle.Release(); }
    }

    public async Task CancelAsync()
    {
        CancelSession();
        AbortReleaseDeferral();
        await _lifecycle.WaitAsync();
        try
        {
            if (_stateMachine.State is RecordingState.Idle or RecordingState.Completed or RecordingState.Failed) return;
            if (_stateMachine.State != RecordingState.Cancelled) _stateMachine.Cancel();
            try
            {
                await _audio.StopAsync(CancellationToken.None);
                CloseRecording();
                await _asr.CancelSessionAsync(CancellationToken.None);
            }
            finally { await DeleteSessionAsync(); }
            Reset();
        }
        catch (OperationCanceledException) when (GetSessionCancellationToken().IsCancellationRequested)
        {
            await DeleteSessionAsync();
            if (_stateMachine.State != RecordingState.Cancelled) _stateMachine.Cancel();
            Reset();
        }
        catch (Exception ex) { await FailAsync(ex); }
        finally { _lifecycle.Release(); }
    }

    internal static async Task<CleanTranscriptResult> CleanWithFallbackAsync(ITranscriptCleaner cleaner, RawTranscript raw, TranscriptStyle style, CancellationToken token) =>
        (await CleanWithFallbackStatusAsync(cleaner, raw, style, token)).Result;

    private static async Task<(CleanTranscriptResult Result, bool UsedFallback)> CleanWithFallbackStatusAsync(
        ITranscriptCleaner cleaner, RawTranscript raw, TranscriptStyle style, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(raw.Text)) throw new InvalidOperationException("Speech recognition returned an empty transcript.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var cleaned = await cleaner.CleanAsync(raw, style, token);
                if (CleanupResultValidator.TryValidate(raw, cleaned, out _)) return (cleaned, false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch when (attempt == 0) { continue; }
            catch { break; }
        }
        return (new CleanTranscriptResult(raw.Text), true);
    }

    private async Task SaveAsync(HistoryEntry entry, CancellationToken token)
    {
        _entry = entry;
        if (_history is not null) await _history.UpdateAsync(entry, token);
    }

    private async Task DeleteSessionAsync()
    {
        CloseRecording();
        if (_entry is not null && _history is not null)
            await _history.DeleteAsync(_entry.Id, deleteAudio: true, CancellationToken.None);
        else if (_recordingPath is not null)
            AsrRetryService.DeleteRecording(_recordingPath);
        _entry = null;
    }

    private async Task FailInsertionAsync(Action showRecovery, DictationErrorCode code = DictationErrorCode.InsertionFailed)
    {
        _stateMachine.TransitionTo(RecordingState.Failed);
        await SaveFailureAsync(code);
        await _overlay.Dispatcher.InvokeAsync(() =>
        {
            showRecovery();
            _overlay.ShowOverlay();
        });
        try { await _asr.CancelSessionAsync(CancellationToken.None); } catch { }
        AbortReleaseDeferral();
        _doubleTap.Reset();
        _stateMachine.Reset();
        _target = null;
    }

    private async Task FailAsync(Exception exception)
    {
        if (_stateMachine.State == RecordingState.Idle)
        {
            await _overlay.Dispatcher.InvokeAsync(() =>
            {
                _overlay.ShowFailure(exception.Message);
                _overlay.ShowOverlay();
            });
            return;
        }
        var code = ErrorFor(_stateMachine.State, exception);
        var emptySpeech = code == DictationErrorCode.AsrFailed
            && string.IsNullOrWhiteSpace(_entry?.RawTranscript);
        if (_stateMachine.State != RecordingState.Failed) _stateMachine.TransitionTo(RecordingState.Failed);
        await _overlay.Dispatcher.InvokeAsync(() =>
        {
            if (emptySpeech)
            {
                _overlay.ShowHint(NoSpeechHint);
                _overlay.ShowOverlay();
                return;
            }
            _overlay.ShowFailure(exception.Message);
            _overlay.ShowOverlay();
        });
        try { await _audio.StopAsync(CancellationToken.None); } catch { }
        CloseRecording();
        try { await _asr.CancelSessionAsync(CancellationToken.None); } catch { }
        await SaveFailureAsync(code);
        AbortReleaseDeferral();
        _doubleTap.Reset();
        _stateMachine.Reset();
        _target = null;
    }

    private async Task SaveFailureAsync(DictationErrorCode code)
    {
        if (_entry is null) return;
        var ended = DateTimeOffset.UtcNow;
        await SaveAsync(_entry with
        {
            RecordingEndedAt = _entry.RecordingEndedAt ?? ended,
            Duration = _entry.Duration ?? (_entry.RecordingStartedAt is { } started ? ended - started : null),
            State = RecordingState.Failed,
            ErrorCode = code,
            TotalDuration = Stopwatch.GetElapsedTime(_started)
        }, CancellationToken.None);
        if (_history is not null) await _history.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        LogLifecycle(_entry);
    }

    private const string NoSpeechHint = "No speech detected — hold Ctrl+Win and record again.";

    private static DictationErrorCode ErrorFor(RecordingState state, Exception exception) =>
        exception is OperationCanceledException ? DictationErrorCode.Cancelled : state switch
        {
            RecordingState.Starting or RecordingState.ListeningPushToTalk or RecordingState.Stopping => DictationErrorCode.AudioCaptureFailed,
            RecordingState.Transcribing => DictationErrorCode.AsrFailed,
            RecordingState.Cleaning => DictationErrorCode.CleanupFailed,
            RecordingState.Inserting => DictationErrorCode.InsertionFailed,
            _ => DictationErrorCode.Interrupted
        };
    private void LogLifecycle(HistoryEntry entry) =>
        _logger.LogInformation(
            "Dictation lifecycle {SessionId} {State} {ErrorCode} {Application} {Category} {InsertionMethod} {RetryCount} {RecordingMs} {AsrMs} {CleanupMs} {InsertionMs} {TotalMs}",
            entry.Id, entry.State, entry.ErrorCode, entry.TargetExecutable, entry.OutputCategory,
            entry.InsertionMethod, entry.RetryCount, entry.Duration?.TotalMilliseconds,
            entry.AsrDuration?.TotalMilliseconds, entry.CleanupDuration?.TotalMilliseconds,
            entry.InsertionDuration?.TotalMilliseconds, entry.TotalDuration?.TotalMilliseconds);


    private static OutputClassification GeneralClassification() => new(OutputContextCategory.General, DefaultStyle, ClassificationSource.General, "GeneralFallback", new ContextDetectionDiagnostic(ContextDetectionConfidence.None, "Fallback"));
    private static bool HasTextTarget(ActiveTarget target) => !string.IsNullOrEmpty(target.FocusedAutomationId) || !string.IsNullOrEmpty(target.FocusedControlType) || target.FocusedChildWindowHandle != 0;
    private void CloseRecording() { _recording?.Dispose(); _recording = null; }
    private CancellationToken GetSessionCancellationToken()
    {
        lock (_sessionCancellationLock) return _sessionCancellation?.Token ?? CancellationToken.None;
    }

    private void ReplaceSessionCancellation()
    {
        lock (_sessionCancellationLock)
        {
            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
        }
    }

    private void CancelSession()
    {
        lock (_sessionCancellationLock) _sessionCancellation?.Cancel();
    }

    private void Reset(bool hideOverlay = true)
    {
        CloseRecording();
        AbortReleaseDeferral();
        _doubleTap.Reset();
        if (_stateMachine.State != RecordingState.Idle) _stateMachine.Reset();
        _target = null;
        CurrentContext = null;
        CurrentClassification = null;
        _style = DefaultStyle;
        _recordingPath = null;
        _sessionOptions = null;
        _asrFailure = null;
        _entry = null;
        if (hideOverlay) _overlay.Dispatcher.Invoke(_overlay.HideOverlay);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelSession();
        AbortReleaseDeferral();
        lock (_sessionCancellationLock)
        {
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
        }
        CloseRecording();
        _lifecycle.Dispose();
    }
}
