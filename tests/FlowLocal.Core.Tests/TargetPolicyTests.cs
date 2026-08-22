using FlowLocal.Core;
using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class TargetPolicyTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0x2000, null, false)]
    [InlineData(null, 0x2000, false)]
    [InlineData(0x2000, 0x1000, true)]
    [InlineData(0x2000, 0x2000, true)]
    [InlineData(0x2000, 0x3000, false)]
    public void IsInjectionSafe_RejectsUnknownOrHigherIntegrity(int? currentRid, int? targetRid, bool expected) =>
        Assert.Equal(expected, TargetPolicy.IsInjectionSafe(currentRid, targetRid));

    [Theory]
    [InlineData("WindowsTerminal", null, true)]
    [InlineData("WindowsTerminal.exe", null, true)]
    [InlineData("wt", null, true)]
    [InlineData("cmd", null, true)]
    [InlineData("powershell", null, true)]
    [InlineData("pwsh", null, true)]
    [InlineData("notepad", "CASCADIA_HOSTING_WINDOW_CLASS", true)]
    [InlineData("notepad", "ConsoleWindowClass", true)]
    [InlineData("notepad", "Notepad", false)]
    [InlineData("", null, false)]
    public void IsTerminal_ClassifiesKnownProcessesAndWindowClasses(
        string executableName,
        string? windowClassName,
        bool expected) =>
        Assert.Equal(expected, TargetPolicy.IsTerminal(executableName, windowClassName));

    [Fact]
    public void IsTerminal_IsCaseInsensitive()
    {
        Assert.True(TargetPolicy.IsTerminal("WINDOWSTERMINAL.EXE", null));
        Assert.True(TargetPolicy.IsTerminal("notepad", "consolewindowclass"));
    }

    [Fact]
    public void IsSameProcessIdentity_RequiresPidAndKnownMatchingStartTime()
    {
        var startedAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

        Assert.True(TargetPolicy.IsSameProcessIdentity(42, 42, startedAt, startedAt));
        Assert.False(TargetPolicy.IsSameProcessIdentity(42, 43, startedAt, startedAt));
        Assert.False(TargetPolicy.IsSameProcessIdentity(42, 42, startedAt, startedAt.AddTicks(1)));
        Assert.False(TargetPolicy.IsSameProcessIdentity(42, 42, null, startedAt));
        Assert.False(TargetPolicy.IsSameProcessIdentity(42, 42, startedAt, null));
        Assert.False(TargetPolicy.IsSameProcessIdentity(42, 42, null, null));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(null, true, false)]
    [InlineData(false, false, false)]
    public void InjectionEligibility_FailsClosedForProtectedOrUnknownFields(
        bool? isPasswordField,
        bool isInjectionSafe,
        bool expected)
    {
        var target = new ActiveTarget(
            42,
            (nint)123,
            "notepad",
            "Notes",
            DateTimeOffset.UnixEpoch,
            IsInjectionSafe: isInjectionSafe,
            IsPasswordField: isPasswordField);

        Assert.Equal(expected, target.IsInjectionSafe && target.IsPasswordField == false);
    }
}
