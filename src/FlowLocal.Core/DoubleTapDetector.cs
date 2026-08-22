namespace FlowLocal.Core;

public enum DoubleTapDecision
{
    Ignore,
    StartPushToTalk,
    ConvertToHandsFree,
    DeferRelease,
    Finalize
}

/// <summary>
/// Pure timing logic that turns raw shortcut press/release events into recording decisions,
/// including hands-free activation by double-tapping the push-to-talk chord.
/// </summary>
public sealed class DoubleTapDetector
{
    private DateTimeOffset? _lastReleaseAt;
    private bool _recording;

    /// <summary>When false every release finalizes immediately (classic push-to-talk).</summary>
    public bool Enabled { get; set; }

    public TimeSpan DoubleTapInterval { get; }

    public DoubleTapDetector(bool enabled, TimeSpan doubleTapInterval)
    {
        if (doubleTapInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(doubleTapInterval));
        Enabled = enabled;
        DoubleTapInterval = doubleTapInterval;
    }

    /// <summary>Records a chord key-down and returns the action to take.</summary>
    public DoubleTapDecision OnPressed(DateTimeOffset timestamp)
    {
        if (_recording) return DoubleTapDecision.Ignore;
        var isSecondTap = Enabled
            && _lastReleaseAt is { } previousRelease
            && timestamp - previousRelease <= DoubleTapInterval;
        _lastReleaseAt = null;
        _recording = true;
        return isSecondTap ? DoubleTapDecision.ConvertToHandsFree : DoubleTapDecision.StartPushToTalk;
    }

    /// <summary>Records a chord key-up. When hands-free is enabled the caller must wait for
    /// either a second tap or deferral expiry before finalizing.</summary>
    public DoubleTapDecision OnReleased(DateTimeOffset timestamp)
    {
        if (!_recording) return DoubleTapDecision.Ignore;
        _recording = false;
        _lastReleaseAt = timestamp;
        return Enabled ? DoubleTapDecision.DeferRelease : DoubleTapDecision.Finalize;
    }

    /// <summary>Called when the double-tap window elapses without a second press.</summary>
    public DoubleTapDecision OnDeferralExpired()
    {
        _lastReleaseAt = null;
        return DoubleTapDecision.Finalize;
    }

    public void Reset()
    {
        _lastReleaseAt = null;
        _recording = false;
    }
}
