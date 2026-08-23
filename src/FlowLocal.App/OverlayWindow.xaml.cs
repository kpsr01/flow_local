using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FlowLocal.App;

public static class ThemeLoader
{
    private const string ThemeUri = "pack://application:,,,/FlowLocal.App;component/Theme.xaml";

    /// <summary>Merges the theme dictionary so StaticResource lookups resolve during XAML parse,
    /// including in hosts without an Application (such as tests).</summary>
    public static void EnsureTheme(System.Windows.ResourceDictionary target)
    {
        if (target.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString == ThemeUri)) return;
        var theme = Application.Current?.Resources.MergedDictionaries
            .FirstOrDefault(dictionary => dictionary.Source?.OriginalString == ThemeUri) ?? new ResourceDictionary { Source = new Uri(ThemeUri) };
        target.MergedDictionaries.Insert(0, theme);
    }
}

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const double DefaultBottomGap = 6.0;
    private const double ScreenEdgeMargin = 10.0;
    private const double ShadowInset = 24.0;
    private static readonly System.Windows.Media.SolidColorBrush PillBorderBrush = CreatePillBorderBrush();

    private static System.Windows.Media.SolidColorBrush CreatePillBorderBrush()
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB3, 0xFF, 0x6B, 0x3D));
        brush.Freeze(); // Shareable across dispatchers (tests create windows on multiple STA threads).
        return brush;
    }

    private static readonly double[] BarMultipliers = [0.45, 0.72, 1.0, 0.72, 0.45];
    private const double BarMinHeight = 4.0;
    private const double BarMaxHeight = 18.0;

    private enum PillMode { Mini, Status, Listening, Processing, Issue }

    private readonly DispatcherTimer _recordingClock;
    private readonly DispatcherTimer _waveClock;
    private readonly DispatcherTimer _completedHideClock;
    private readonly DispatcherTimer _hintHideClock;
    private readonly double[] _barHeights = new double[5];
    private DateTimeOffset _recordingStartedAt = DateTimeOffset.UtcNow;
    private long _lastLevelUpdateTicks;
    private float _latestLevel;
    private Storyboard? _activeThinking;
    private PillMode _mode = PillMode.Mini;
    private bool _listeningVisualsActive;
    private bool _placed;
    private double _anchorX;
    private double _anchorBottomY;

    public event EventHandler? RetryRequested;
    public event EventHandler? OpenHistoryRequested;
    public event EventHandler? OpenAppRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? CancelRequested;

    public OverlayWindow()
    {
        ThemeLoader.EnsureTheme(Resources);
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNonActivatingStyle();

        _recordingClock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _recordingClock.Tick += (_, _) => UpdateDurationText();

        _waveClock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _waveClock.Tick += (_, _) => AdvanceWaveform();

        _completedHideClock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        _completedHideClock.Tick += (_, _) =>
        {
            _completedHideClock.Stop();
            CollapseToIdle();
        };

        _hintHideClock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _hintHideClock.Tick += (_, _) =>
        {
            _hintHideClock.Stop();
            CollapseToIdle();
        };
    }

    public void ShowInitializing() { SetStatus("Initializing speech model"); EnterMode(PillMode.Status); }

    public void ShowModelSetup() { SetStatus("Preparing speech model…"); EnterMode(PillMode.Status); }

    public void ShowReady()
    {
        SetStatus("Ready");
        EnterMode(PillMode.Mini);
    }

    public void ShowListening() => ShowListening(DateTimeOffset.UtcNow);

    public void ShowListening(DateTimeOffset recordingStartedAt)
    {
        SetStatus("Listening");
        StartRecordingFeedback(recordingStartedAt);
    }

    public void ShowHandsFree(DateTimeOffset recordingStartedAt)
    {
        SetStatus("Recording");
        StartRecordingFeedback(recordingStartedAt);
    }

    public void UpdateAudioLevel(float level)
    {
        if (!_listeningVisualsActive) return;
        var now = Environment.TickCount64;
        if (now - _lastLevelUpdateTicks < 24) return;
        _lastLevelUpdateTicks = now;
        _latestLevel = Math.Clamp(level, 0f, 1f);
    }

    public void ShowTranscribing() => EnterThinking("Transcribing");

    public void ShowCleaning() => EnterThinking("Cleaning transcript");

    public void ShowInserting() => EnterThinking("Inserting");

    public void ShowCompleted()
    {
        SetStatus("Completed");
        EnterMode(PillMode.Issue);
        IssueIcon.Text = "\uE73E";
        IssueIcon.Foreground = SuccessBrush();
        _completedHideClock.Stop();
        _completedHideClock.Start();
    }

    public void ShowFailure(string message)
    {
        SetStatus(string.IsNullOrWhiteSpace(message) ? "Failed" : message);
        EnterMode(PillMode.Issue);
        IssueIcon.Text = "\uE783";
        IssueIcon.Foreground = DangerBrush();
        ShowActivated = true;
        Focusable = true;
        ApplyNonActivatingStyle(false);
        Activate();
        RetryButton.Focus();
    }

    public void ShowNoTextTarget() => ShowIssue("No text target", warning: true);

    public void ShowInputBlocked() => ShowIssue("Input blocked (elevated or protected target)", warning: true);

    public void ShowInsertionFailed() => ShowIssue("Insertion failed", warning: true);

    public void ShowCopiedToClipboard() => ShowIssue("Copied to clipboard — paste manually", warning: true);

    /// <summary>Non-fatal coaching status (e.g., "no speech — record again").</summary>
    public void ShowHint(string message)
    {
        ShowIssue(message, warning: true);
        _hintHideClock.Stop();
        _hintHideClock.Start();
    }

    public void ShowDetectedCategory(string category) =>
        ShowReady();

    public void ShowOverlay()
    {
        Topmost = true;
        if (_placed && IsVisible) return; // Already docked — mode changes morph in place.
        PlaceInWorkArea();
        Show();
        PlayIntro();
    }

    public void HideOverlay()
    {
        StopAllMotion();
        Hide();
    }

    private void CollapseToIdle()
    {
        SetStatus("Ready");
        EnterMode(PillMode.Mini);
    }

    private static System.Windows.Media.Brush EmberDot() => Dot(0xFF, 0x6B, 0x3D);
    private static System.Windows.Media.Brush SuccessBrush() => Dot(0x4C, 0xC3, 0x8A);
    private static System.Windows.Media.Brush WarnBrush() => Dot(0xFF, 0xB3, 0x5C);
    private static System.Windows.Media.Brush DangerBrush() => Dot(0xFF, 0x6B, 0x5E);

    private static System.Windows.Media.SolidColorBrush Dot(byte r, byte g, byte b) =>
        new(System.Windows.Media.Color.FromRgb(r, g, b));

    private void StartRecordingFeedback(DateTimeOffset recordingStartedAt)
    {
        _recordingStartedAt = recordingStartedAt;
        _listeningVisualsActive = true;
        _latestLevel = 0f;
        for (var i = 0; i < _barHeights.Length; i++) _barHeights[i] = BarMinHeight;
        ApplyBarHeights();
        DurationText.Text = "0:00";
        EnterMode(PillMode.Listening);
        UpdateDurationText();
        _recordingClock.Stop();
        _recordingClock.Start();
        _waveClock.Stop();
        _waveClock.Start();
    }

    private void EnterThinking(string status)
    {
        SetStatus(status);
        EnterMode(PillMode.Processing);
    }

    private void ShowIssue(string message, bool warning)
    {
        SetStatus(message);
        EnterMode(PillMode.Issue);
        IssueIcon.Text = "\uE783";
        IssueIcon.Foreground = warning ? WarnBrush() : DangerBrush();
        if (warning)
        {
            ShowActivated = false;
            Focusable = false;
            ApplyNonActivatingStyle(true);
        }
    }

    private void EnterMode(PillMode mode)
    {
        var wasVisible = IsVisible && _placed;
        var oldWidth = ActualWidth;
        var oldHeight = ActualHeight;
        var changed = mode != _mode;
        _mode = mode;
        _completedHideClock.Stop();
        _hintHideClock.Stop();

        // Idle pill is plain until hover — the mic glyph only appears via Root_MouseEnter.
        MiniDot.Visibility = Visibility.Collapsed;
        ActiveRow.Visibility = mode == PillMode.Mini ? Visibility.Collapsed : Visibility.Visible;
        // One pill identity in every state: same radius + ember outline, so expanding
        // never looks like a different popup. Idle just enforces the resting footprint.
        Root.CornerRadius = new CornerRadius(14);
        Root.BorderBrush = PillBorderBrush;
        if (mode == PillMode.Mini)
        {
            Root.MinWidth = 68;
            Root.MinHeight = 24;
        }
        else
        {
            Root.MinWidth = 0;
            Root.MinHeight = 0;
        }

        var listening = mode == PillMode.Listening;
        CancelButton.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
        WavePanel.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;

        ThinkingDots.Visibility = mode == PillMode.Processing ? Visibility.Visible : Visibility.Collapsed;

        var issue = mode == PillMode.Issue;
        ActionPanel.Visibility = issue ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = issue ? Visibility.Visible : Visibility.Collapsed;
        IssueIcon.Visibility = issue ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = mode is PillMode.Status or PillMode.Issue
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!listening) StopWaveAndClocks();
        if (mode == PillMode.Processing) StartThinking(); else StopThinking();
        if (changed && wasVisible) PlayMorph(oldWidth, oldHeight);
    }

    /// <summary>Grows/shrinks the pill smoothly from its previous on-screen footprint,
    /// anchored at bottom-center, so state changes morph instead of blink.</summary>
    private void PlayMorph(double oldWidth, double oldHeight)
    {
        UpdateLayout();
        var newWidth = ActualWidth;
        var newHeight = ActualHeight;
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;
        if (newWidth < 1 || newHeight < 1 || oldWidth < 1 || oldHeight < 1) return;
        if (Math.Abs(newWidth - oldWidth) < 2 && Math.Abs(newHeight - oldHeight) < 2) return;
        if (!MotionAllowed()) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(240));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation(1, duration) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(1, duration) { EasingFunction = ease };
        // Start near the old footprint (clamped) instead of the exact ratio, so
        // big jumps like idle→recording grow outward instead of popping from a sliver.
        RootScale.ScaleX = Math.Clamp(oldWidth / newWidth, 0.7, 1.3);
        RootScale.ScaleY = Math.Clamp(oldHeight / newHeight, 0.7, 1.3);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleX);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleY);
    }


    private void StartThinking()
    {
        StopThinking();
        if (!MotionAllowed())
        {
            DotA.Opacity = DotB.Opacity = DotC.Opacity = 0.8;
            return;
        }
        _activeThinking = (Storyboard)Resources["ThinkingStoryboard"];
        _activeThinking.Begin(this, true);
    }

    private void StopThinking()
    {
        _activeThinking?.Remove(this);
        _activeThinking = null;
        DotA.Opacity = DotB.Opacity = DotC.Opacity = 1;
    }

    private void StopAllMotion()
    {
        StopThinking();
        _completedHideClock.Stop();
        _hintHideClock.Stop();
    }

    private void StopWaveAndClocks()
    {
        _listeningVisualsActive = false;
        _recordingClock.Stop();
        _waveClock.Stop();
    }

    private bool MotionAllowed() => SystemParameters.ClientAreaAnimation;

    private void PlayIntro()
    {
        Root.Opacity = 1;
        RootTranslate.Y = 0;
        if (!MotionAllowed()) return;
        var intro = (Storyboard)Resources["IntroStoryboard"];
        intro.Begin(this, true);
    }

    private void AdvanceWaveform()
    {
        const double attack = 0.42;
        const double release = 0.16;
        for (var i = 0; i < _barHeights.Length; i++)
        {
            var target = BarMinHeight + _latestLevel * BarMultipliers[i] * (BarMaxHeight - BarMinHeight);
            var easing = target > _barHeights[i] ? attack : release;
            _barHeights[i] += (target - _barHeights[i]) * easing;
        }
        ApplyBarHeights();
    }

    private void ApplyBarHeights()
    {
        Bar0.Height = _barHeights[0];
        Bar1.Height = _barHeights[1];
        Bar2.Height = _barHeights[2];
        Bar3.Height = _barHeights[3];
        Bar4.Height = _barHeights[4];
    }

    private void UpdateDurationText()
    {
        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        DurationText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        StatusText.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            $"FlowLocal dictation status: {status}");
    }

    private (double ScaleX, double ScaleY) DpiScale()
    {
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            return (dpi.DpiScaleX == 0 ? 1 : dpi.DpiScaleX, dpi.DpiScaleY == 0 ? 1 : dpi.DpiScaleY);
        }
        catch (InvalidOperationException)
        {
            return (1, 1);
        }
    }

    private Rect WorkAreaInDips(System.Drawing.Rectangle physicalArea, (double X, double Y) dpi)
    {
        return new Rect(
            physicalArea.Left / dpi.X,
            physicalArea.Top / dpi.Y,
            physicalArea.Width / dpi.X,
            physicalArea.Height / dpi.Y);
    }

    private Rect _placementBounds;

    private void PlaceInWorkArea()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var (sx, sy) = DpiScale();
        var area = WorkAreaInDips(System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea, (sx, sy));
        _placementBounds = area;

        // Anchor: center-X of the work area, visible pill bottom DefaultBottomGap
        // above the taskbar. ShadowInset compensates the transparent window margin,
        // so the drawn border — not the invisible window edge — hugs the taskbar.
        // Independent of window size, so startup (before first layout) is exact.
        _anchorX = area.Left + area.Width / 2;
        _anchorBottomY = area.Bottom - DefaultBottomGap + ShadowInset;
        _placed = true;
        SnapToAnchor();
    }

    private void SnapToAnchor()
    {
        Left = Math.Clamp(_anchorX - ActualWidth / 2,
            _placementBounds.Left + ScreenEdgeMargin,
            Math.Max(_placementBounds.Left + ScreenEdgeMargin, _placementBounds.Right - ActualWidth - ScreenEdgeMargin));
        Top = _anchorBottomY - ActualHeight;
    }


    private void OverlayWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_placed) return;
        SnapToAnchor();
    }
    private void Root_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_mode != PillMode.Mini) return;
        MiniDot.Visibility = Visibility.Visible;
    }

    private void Root_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_mode != PillMode.Mini) return;
        MiniDot.Visibility = Visibility.Collapsed;
    }

    private void Root_ClickStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_mode != PillMode.Mini || e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Retry_Click(object sender, RoutedEventArgs e) =>
        RetryRequested?.Invoke(this, EventArgs.Empty);

    private void OpenHistory_Click(object sender, RoutedEventArgs e) =>
        OpenHistoryRequested?.Invoke(this, EventArgs.Empty);

    private void OpenApp_Click(object sender, RoutedEventArgs e) =>
        OpenAppRequested?.Invoke(this, EventArgs.Empty);

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Finishing");
        EnterMode(PillMode.Processing);
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyNonActivatingStyle(bool nonActivating = true)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(nonActivating ? style | WsExNoActivate : style & ~WsExNoActivate));
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
}
