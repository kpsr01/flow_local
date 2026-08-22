using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class SendInputInsertionTests
{
    [Fact]
    public void TryInsert_EmitsUnicodeCodeUnitsInOrderWithoutEnter()
    {
        InputNativeMethods.Input[]? emitted = null;
        var insertion = new SendInputInsertion(inputs =>
        {
            emitted = inputs;
            return (uint)inputs.Length;
        });

        var result = insertion.TryInsert(SafeTarget(), "A😀");

        Assert.Equal(InsertionDisposition.Inserted, result.Disposition);
        Assert.NotNull(emitted);
        Assert.Equal(6, emitted.Length);
        Assert.Equal(new ushort[] { 'A', 'A', 0xD83D, 0xD83D, 0xDE00, 0xDE00 },
            emitted.Select(input => input.Data.Keyboard.ScanCode));
        Assert.All(emitted, input =>
        {
            Assert.Equal(InputNativeMethods.InputKeyboard, input.Type);
            Assert.Equal((ushort)0, input.Data.Keyboard.VirtualKey);
            Assert.NotEqual(0u, input.Data.Keyboard.Flags & InputNativeMethods.KeyEventUnicode);
        });
        Assert.Equal(new uint[]
        {
            InputNativeMethods.KeyEventUnicode,
            InputNativeMethods.KeyEventUnicode | InputNativeMethods.KeyEventKeyUp,
            InputNativeMethods.KeyEventUnicode,
            InputNativeMethods.KeyEventUnicode | InputNativeMethods.KeyEventKeyUp,
            InputNativeMethods.KeyEventUnicode,
            InputNativeMethods.KeyEventUnicode | InputNativeMethods.KeyEventKeyUp
        }, emitted.Select(input => input.Data.Keyboard.Flags));
    }

    [Theory]
    [MemberData(nameof(RestrictedTargets))]
    public void TryInsert_RejectsRestrictedTargetsWithoutSending(ActiveTarget target)
    {
        var called = false;
        var result = new SendInputInsertion(_ =>
        {
            called = true;
            return 0;
        }).TryInsert(target, "text");

        Assert.Equal(InsertionDisposition.Unsupported, result.Disposition);
        Assert.False(called);
    }

    [Fact]
    public void TryInsert_PartialSendReportsUnknownSideEffect()
    {
        var result = new SendInputInsertion(_ => 1).TryInsert(SafeTarget(), "x");

        Assert.Equal(InsertionDisposition.UnknownSideEffect, result.Disposition);
    }

    public static TheoryData<ActiveTarget> RestrictedTargets() => new()
    {
        SafeTarget() with { IsTerminal = true },
        SafeTarget() with { IsPasswordField = true },
        SafeTarget() with { IsPasswordField = null },
        SafeTarget() with { CurrentIntegrityRid = 0x2000, TargetIntegrityRid = 0x3000 },
        SafeTarget() with { CurrentIntegrityRid = null },
        SafeTarget() with { IsInjectionSafe = false }
    };

    private static ActiveTarget SafeTarget() => new(
        42,
        (nint)123,
        "notepad",
        "Notes",
        DateTimeOffset.UnixEpoch,
        CurrentIntegrityRid: 0x2000,
        TargetIntegrityRid: 0x2000,
        IsInjectionSafe: true,
        IsPasswordField: false);
}
