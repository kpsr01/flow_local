using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlowLocal.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Forms = System.Windows.Forms;

namespace FlowLocal.App;

public partial class App : Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _settingsWindow;
    private OverlayWindow? _overlayWindow;
    private GlobalShortcutService? _shortcut;
    private WasapiAudioCaptureService? _audio;
    private DictationController? _dictation;
    private CanaryAsrService? _asr;
    private SottoTranscriptCleaner? _cleaner;
    private SqliteHistoryRepository? _history;
    private AppSettingsStore? _appSettings;
    private HistoryActionService? _historyActions;
    private bool _retryBusy;
    private bool _dictationReady;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activationSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The tray app must survive UI-layer faults: log, surface on the overlay, keep running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "Unknown failure"));

        // Single instance: relaunching from Start/search/taskbar just surfaces the main window.
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FlowLocal.ShowMain");
        _singleInstance = new Mutex(true, @"Local\FlowLocal.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            _activationSignal.Set();
            Shutdown();
            return;
        }
        _ = Task.Run(() =>
        {
            while (_activationSignal.WaitOne()) Dispatcher.Invoke(ShowSettings);
        });

        var showMainWindow = !e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        _settingsWindow = new MainWindow();
        MainWindow = _settingsWindow;
        _settingsWindow.Icon = FlowIcon.CreateImageSource();
        _overlayWindow = new OverlayWindow();
        _overlayWindow.Icon = FlowIcon.CreateImageSource();
        _overlayWindow.ShowInitializing();
        _overlayWindow.RetryRequested += OnOverlayRetryRequested;
        _overlayWindow.OpenAppRequested += (_, _) => ShowSettings();
        _overlayWindow.OpenHistoryRequested += (_, _) => ShowHistory();
        _overlayWindow.StartRequested += OnPillStartRequested;
        _overlayWindow.StopRequested += OnOverlayStopRequested;
        _overlayWindow.CancelRequested += OnOverlayCancelRequested;
        _overlayWindow.ExitRequested += (_, _) => ExitApplication();
        _overlayWindow.ShowOverlay();
        _audio = new WasapiAudioCaptureService();
        _audio.LevelChanged += OnAudioLevelChanged;
        _audio.FellBackToDefaultDevice += OnMicrophoneFallback;
        _asr = new CanaryAsrService();
        _cleaner = new SottoTranscriptCleaner();
        _history = new SqliteHistoryRepository();
        _appSettings = new AppSettingsStore();
        var targets = new ActiveTargetTracker();
        var contextDetector = new ApplicationContextDetector(new BrowserContextDetector());
        var styleClassifier = new OutputStyleClassifier();
        var styleOverrides = new JsonStyleOverrideStore();
        var insertion = new ClipboardTextInsertionService();
        _dictation = new DictationController(
            new RecordingStateMachine(), targets, contextDetector, styleClassifier, styleOverrides,
            _audio, _asr, _cleaner, _cleaner, insertion, _overlayWindow,
            NullLogger<DictationController>.Instance, _history,
            asrModelName: CanaryAsrService.ModelName);
        var historyActions = new HistoryActionService(_history, _asr, _cleaner, targets, insertion);
        _historyActions = historyActions;
        ApplicationContext? diagnosticContext = null;
        OutputClassification? diagnosticClassification = null;
        _settingsWindow.ConfigureApplicationStyles(
            styleOverrides,
            () => _dictation.CurrentContext ?? diagnosticContext,
            () => _dictation.CurrentClassification ?? diagnosticClassification,
            async cancellationToken =>
            {
                var target = await targets.CaptureAsync(cancellationToken);
                var settings = await styleOverrides.LoadAsync(cancellationToken);
                diagnosticContext = await contextDetector.DetectAsync(
                    target, settings.Settings.WebsiteDetectionEnabled, cancellationToken);
                diagnosticClassification = styleClassifier.Classify(diagnosticContext, settings.Settings);
            });

        _shortcut = new GlobalShortcutService();
        _shortcut.Pressed += OnShortcutPressed;
        _shortcut.Released += OnShortcutReleased;
        _shortcut.Cancelled += OnShortcutCancelled;

        var menu = new Forms.ContextMenuStrip { AccessibleName = "FlowLocal tray menu" };
        menu.Items.Add("&Settings", null, (_, _) => ShowSettings()).AccessibleName = "Open FlowLocal settings";
        menu.Items.Add("&History", null, (_, _) => ShowHistory()).AccessibleName = "Open dictation history";
        menu.Items.Add("E&xit", null, (_, _) => ExitApplication()).AccessibleName = "Exit FlowLocal";
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = FlowIcon.CreateTrayIcon(),
            Text = "FlowLocal dictation",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();
        _ = InitializeAsync(historyActions);
        if (showMainWindow) ShowSettings();
    }

    private async Task InitializeAsync(HistoryActionService actions)
    {
        if (_dictation is null || _overlayWindow is null || _history is null || _settingsWindow is null) return;
        try
        {
            _overlayWindow.ShowModelSetup();
            await _history.InitializeAsync(CancellationToken.None);
            await _history.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await _settingsWindow.ConfigureHistoryAsync(_history, actions.ExecuteAsync, CancellationToken.None);
            if (_appSettings is not null && _shortcut is not null && _audio is not null && _asr is not null && _cleaner is not null)
            {
                await _settingsWindow.ConfigureRuntimeAsync(
                    _appSettings, _shortcut, _audio, _asr, _cleaner, ApplyAppSettings);
                var settings = await _appSettings.LoadAsync();
                ApplyAppSettings(settings);
            }
            var recovery = new CrashRecoveryService(_history);
            var entries = await recovery.ScanAsync(CancellationToken.None);
            if (entries.Count > 0)
            {
                var choice = await _settingsWindow.PromptRecoveryAsync(entries, CancellationToken.None);
                if (choice == RecoveryChoice.Recover)
                {
                    ShowHistory(entries[0].Id);
                }
                else if (choice == RecoveryChoice.Delete)
                {
                    foreach (var entry in entries) await recovery.DeleteAsync(entry, CancellationToken.None);
                    await _settingsWindow.RefreshHistoryAsync();
                }
            }
            await _dictation.InitializeAsync();
            _dictationReady = true;
            // Wispr-style persistent idle pill: collapses to a tiny mic glyph instead of hiding.
            _overlayWindow.ShowReady();
        }
        catch (Exception exception)
        {
            _overlayWindow.ShowFailure($"Dictation initialization failed: {exception.Message}");
            _overlayWindow.ShowOverlay();
        }
    }

    private void ApplyAppSettings(AppSettings settings)
    {
        _shortcut?.Configure(settings.ShortcutModifiers ?? AppSettings.DefaultShortcutModifiers);
        if (_dictation is not null)
        {
            _dictation.HandsFreeEnabled = settings.HandsFreeEnabled;
            _dictation.DoubleTapInterval = TimeSpan.FromMilliseconds(settings.DoubleTapIntervalMilliseconds);
        }
        if (_audio is not null)
        {
            _audio.FollowDefaultDevice = settings.FollowDefaultMicrophone;
            _audio.PreferredDeviceId = settings.PreferredMicrophoneDeviceId;
        }
        _settingsWindow?.RefreshRuntimeDiagnostics();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        AuditProblemBorders();
        try
        {
            _overlayWindow?.ShowFailure($"UI error contained: {e.Exception.Message}");
            _overlayWindow?.ShowOverlay();
        }
        catch (Exception)
        {
        }
        e.Handled = true;
    }

    private static void AuditProblemBorders()
    {
        try
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("---- border audit ----");
            if (Application.Current is not null)
            {
                foreach (Window window in Application.Current.Windows)
                {
                    report.AppendLine($"WINDOW '{window.Title}'");
                    WalkBorders(window, report);
                }
            }
            AppendCrashLog(report.ToString());
        }
        catch (Exception)
        {
        }
    }

    private static void WalkBorders(DependencyObject node, System.Text.StringBuilder report)
    {
        if (node is System.Windows.Controls.Border border)
        {
            string state;
            try
            {
                var local = border.ReadLocalValue(System.Windows.Controls.Border.BackgroundProperty);
                System.Windows.Media.Brush? effective;
                try
                {
                    effective = border.Background;
                    state = $"local={(local == DependencyProperty.UnsetValue ? "UNSET" : local?.GetType().Name ?? "null")} ok={effective?.GetType().Name ?? "null"}";
                }
                catch (Exception inner)
                {
                    state = $"local={(local == DependencyProperty.UnsetValue ? "UNSET" : local?.GetType().Name ?? "null")} GET_THROWS({inner.Message})";
                }

                if (state.Contains("UNSET") || state.Contains("THROWS"))
                {
                    report.AppendLine($"  !! Border Name='{border.Name}' {state}");
                }
            }
            catch (Exception outer)
            {
                report.AppendLine($"  ?? Border Name='{border.Name}' audit-failed {outer.Message}");
            }
        }

        var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < children; i++)
        {
            WalkBorders(System.Windows.Media.VisualTreeHelper.GetChild(node, i), report);
        }
    }

    private static void LogCrash(Exception? exception)
    {
        AppendCrashLog($"{DateTimeOffset.Now:O}  {exception}\n\n");
    }

    private static void AppendCrashLog(string content)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlowLocal", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), content);
        }
        catch (Exception)
        {
        }
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e) =>
        _overlayWindow?.UpdateAudioLevel(e.Level);

    private void OnMicrophoneFallback(object? sender, string message) =>
        _notifyIcon?.ShowBalloonTip(5000, "FlowLocal microphone", message, Forms.ToolTipIcon.Warning);

    private async void OnShortcutPressed(object? sender, EventArgs e) =>
        await RunControllerAsync(_dictationReady && _dictation is not null ? _dictation.HandleShortcutPressedAsync : ShowBackendUnavailableAsync);
    private async void OnShortcutReleased(object? sender, EventArgs e) =>
        await RunControllerAsync(_dictationReady && _dictation is not null ? _dictation.HandleShortcutReleasedAsync : null);
    private async void OnShortcutCancelled(object? sender, EventArgs e) => await RunControllerAsync(_dictationReady && _dictation is not null ? _dictation.CancelAsync : null);

    private async void OnOverlayRetryRequested(object? sender, EventArgs e)
    {
        if (_retryBusy || !(_dictationReady && _history is not null && _historyActions is not null)) return;
        _retryBusy = true;
        try
        {
            var failed = (await _history.QueryAsync(
                    new HistoryQuery(FailedOnly: true),
                    CancellationToken.None))
                .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.AudioFilePath) && entry.State == RecordingState.Failed);
            if (failed is null)
            {
                _overlayWindow!.ShowFailure("The failed session has no saved recording to retry.");
                return;
            }

            _overlayWindow!.ShowTranscribing();
            await _historyActions.RetryAsrAsync(failed);
            var transcribed = await _history.GetAsync(failed.Id, CancellationToken.None).ConfigureAwait(true)
                ?? failed;

            _overlayWindow.ShowCleaning();
            await _historyActions.RetryCleanupAsync(transcribed);
            var cleaned = await _history.GetAsync(failed.Id, CancellationToken.None).ConfigureAwait(true)
                ?? transcribed;

            _overlayWindow.ShowInserting();
            await _historyActions.RetryInsertionAsync(cleaned);
            _overlayWindow.ShowCompleted();
            _ = _settingsWindow?.RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            var message = exception.Message;
            if (message.Contains("No speech", StringComparison.OrdinalIgnoreCase)
                || message.Contains("empty transcript", StringComparison.OrdinalIgnoreCase))
            {
                _overlayWindow?.ShowHint("Could not hear speech clearly — hold Ctrl+Win and record again.");
                _overlayWindow?.ShowOverlay();
            }
            else
            {
                _overlayWindow?.ShowFailure(message);
            }
        }
        finally
        {
            _retryBusy = false;
        }
    }

    private void OnPillStartRequested(object? sender, EventArgs e)
    {
        if (!_dictationReady || _dictation is null) return;
        _ = _dictation.HandleShortcutPressedAsync();
    }

    private async void OnOverlayStopRequested(object? sender, EventArgs e)
    {
        if (_dictationReady && _dictation is not null) await _dictation.ReleaseAsync();
    }

    private async void OnOverlayCancelRequested(object? sender, EventArgs e)
    {
        if (_dictationReady && _dictation is not null) await _dictation.CancelAsync();
    }

    private Task ShowBackendUnavailableAsync()
    {
        _overlayWindow?.ShowFailure("The speech model is not ready. Dictation is unavailable.");
        _overlayWindow?.ShowOverlay();
        return Task.CompletedTask;
    }
    private static async Task RunControllerAsync(Func<Task>? action) { if (action is not null) await action(); }

    private void ShowSettings()
    {
        if (_settingsWindow is null) return;
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ShowHistory(Guid? selectedId = null)
    {
        ShowSettings();
        _settingsWindow?.ShowHistory(selectedId);
    }

    private void ExitApplication()
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _dictationReady = false;
        _notifyIcon?.Dispose();
        _shortcut?.Dispose();
        _dictation?.Dispose();
        if (_audio is not null)
        {
            _audio.LevelChanged -= OnAudioLevelChanged;
            _audio.FellBackToDefaultDevice -= OnMicrophoneFallback;
        }
        _audio?.Dispose();
        _cleaner?.Dispose();
        if (_asr is not null) await _asr.DisposeAsync();
        _activationSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
