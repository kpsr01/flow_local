using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class ActiveTargetTests
{
    [Fact]
    public void LegacyConstructor_DefaultsOptionalMetadataFailClosed()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

        var target = new ActiveTarget(42, (nint)123, "notepad", "Notes", capturedAt);

        Assert.Equal(42, target.ProcessId);
        Assert.Equal((nint)123, target.WindowHandle);
        Assert.Equal("notepad", target.ExecutableName);
        Assert.Equal("Notes", target.WindowTitle);
        Assert.Equal(capturedAt, target.CapturedAt);
        Assert.Equal(0u, target.WindowThreadId);
        Assert.Equal(nint.Zero, target.FocusedChildWindowHandle);
        Assert.Null(target.ProcessStartTime);
        Assert.Null(target.ExecutablePath);
        Assert.Empty(target.WindowClassName);
        Assert.Null(target.CurrentIntegrityRid);
        Assert.Null(target.TargetIntegrityRid);
        Assert.False(target.IsInjectionSafe);
        Assert.False(target.IsTerminal);
        Assert.Null(target.FocusedAutomationId);
        Assert.Null(target.FocusedControlType);
        Assert.Null(target.FocusedName);
        Assert.Null(target.IsPasswordField);
    }

    [Fact]
    public void Metadata_IsPreservedExactly()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var startedAt = capturedAt.AddHours(-1);

        var target = new ActiveTarget(
            42, (nint)123, "WindowsTerminal", "PowerShell", capturedAt,
            7, (nint)456, startedAt, @"C:\Program Files\WindowsApps\wt.exe",
            "CASCADIA_HOSTING_WINDOW_CLASS", 0x2000, 0x2000, true, true,
            "TerminalControl", "Edit", "PowerShell", false);

        Assert.Equal(7u, target.WindowThreadId);
        Assert.Equal((nint)456, target.FocusedChildWindowHandle);
        Assert.Equal(startedAt, target.ProcessStartTime);
        Assert.Equal(@"C:\Program Files\WindowsApps\wt.exe", target.ExecutablePath);
        Assert.Equal("CASCADIA_HOSTING_WINDOW_CLASS", target.WindowClassName);
        Assert.Equal(0x2000, target.CurrentIntegrityRid);
        Assert.Equal(0x2000, target.TargetIntegrityRid);
        Assert.True(target.IsInjectionSafe);
        Assert.True(target.IsTerminal);
        Assert.Equal("TerminalControl", target.FocusedAutomationId);
        Assert.Equal("Edit", target.FocusedControlType);
        Assert.Equal("PowerShell", target.FocusedName);
        Assert.False(target.IsPasswordField);
    }
}
