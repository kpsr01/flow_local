using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class DoubleTapDetectorTests
{
    [Fact]
    public void Disabled_ReleaseFinalizesImmediately()
    {
        var detector = new DoubleTapDetector(enabled: false, doubleTapInterval: TimeSpan.FromMilliseconds(400));
        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal(DoubleTapDecision.StartPushToTalk, detector.OnPressed(t0));
        Assert.Equal(DoubleTapDecision.Finalize, detector.OnReleased(t0.AddMilliseconds(50)));
    }

    [Fact]
    public void Enabled_FirstReleaseDefersUntilSecondTap()
    {
        var detector = new DoubleTapDetector(enabled: true, doubleTapInterval: TimeSpan.FromMilliseconds(400));
        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal(DoubleTapDecision.StartPushToTalk, detector.OnPressed(t0));
        Assert.Equal(DoubleTapDecision.DeferRelease, detector.OnReleased(t0.AddMilliseconds(30)));
        Assert.Equal(DoubleTapDecision.ConvertToHandsFree, detector.OnPressed(t0.AddMilliseconds(120)));
    }

    [Fact]
    public void Enabled_DeferralExpiryFinalizes()
    {
        var detector = new DoubleTapDetector(enabled: true, doubleTapInterval: TimeSpan.FromMilliseconds(400));
        var t0 = DateTimeOffset.UtcNow;

        detector.OnPressed(t0);
        Assert.Equal(DoubleTapDecision.DeferRelease, detector.OnReleased(t0.AddMilliseconds(30)));
        Assert.Equal(DoubleTapDecision.Finalize, detector.OnDeferralExpired());
    }

    [Fact]
    public void Enabled_SecondTapOutsideIntervalStartsNewSession()
    {
        var detector = new DoubleTapDetector(enabled: true, doubleTapInterval: TimeSpan.FromMilliseconds(200));
        var t0 = DateTimeOffset.UtcNow;

        detector.OnPressed(t0);
        Assert.Equal(DoubleTapDecision.DeferRelease, detector.OnReleased(t0.AddMilliseconds(20)));
        Assert.Equal(DoubleTapDecision.StartPushToTalk, detector.OnPressed(t0.AddMilliseconds(500)));
    }

    [Fact]
    public void Enabled_IntervalBoundaryStillCountsAsDoubleTap()
    {
        var detector = new DoubleTapDetector(enabled: true, doubleTapInterval: TimeSpan.FromMilliseconds(300));
        var t0 = DateTimeOffset.UtcNow;

        detector.OnPressed(t0);
        detector.OnReleased(t0.AddMilliseconds(10));
        Assert.Equal(
            DoubleTapDecision.ConvertToHandsFree,
            detector.OnPressed(t0.AddMilliseconds(300)));
    }

    [Fact]
    public void PressWhileRecordingIsIgnored()
    {
        var detector = new DoubleTapDetector(enabled: true, doubleTapInterval: TimeSpan.FromMilliseconds(400));
        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal(DoubleTapDecision.StartPushToTalk, detector.OnPressed(t0));
        Assert.Equal(DoubleTapDecision.Ignore, detector.OnPressed(t0.AddMilliseconds(10)));
        Assert.Equal(DoubleTapDecision.DeferRelease, detector.OnReleased(t0.AddMilliseconds(20)));
    }

    [Fact]
    public void ReleaseWithoutSessionIsIgnored()
    {
        var detector = new DoubleTapDetector(enabled: false, doubleTapInterval: TimeSpan.Zero);

        Assert.Equal(DoubleTapDecision.Ignore, detector.OnReleased(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ResetClearsPendingState()
    {
        var detector = new DoubleTapDetector(enabled: true, doubleTapInterval: TimeSpan.FromMilliseconds(400));
        var t0 = DateTimeOffset.UtcNow;

        detector.OnPressed(t0);
        detector.Reset();

        Assert.Equal(DoubleTapDecision.Ignore, detector.OnReleased(t0.AddMilliseconds(10)));
        Assert.Equal(DoubleTapDecision.StartPushToTalk, detector.OnPressed(t0.AddMilliseconds(20)));
    }

    [Theory]
    [InlineData(-1)]
    public void NegativeIntervalRejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DoubleTapDetector(true, TimeSpan.FromMilliseconds(milliseconds)));
    }
}
