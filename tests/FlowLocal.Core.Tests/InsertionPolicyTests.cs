using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class InsertionPolicyTests
{
    [Fact]
    public async Task InsertAsync_FallsBackInOrderAndStopsAtSuccess()
    {
        var calls = new List<string>();
        var service = Service(
            restore: () => { calls.Add("restore"); return true; },
            direct: () => { calls.Add("direct"); return InsertionAttempt.Unsupported(); },
            paste: () => { calls.Add("paste"); return InsertionAttempt.Failed("paste failed"); },
            send: () => { calls.Add("send"); return InsertionAttempt.Inserted(); },
            copy: _ => { calls.Add("copy"); return InsertionAttempt.Inserted(); });

        var result = await service.InsertAsync(SafeTarget(), "text", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TextInsertionMethod.SendInput, result.Method);
        Assert.Equal(["restore", "direct", "paste", "send"], calls);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("paste")]
    public async Task InsertAsync_StopsAfterUnknownSideEffect(string uncertainLayer)
    {
        var calls = new List<string>();
        var service = Service(
            direct: () =>
            {
                calls.Add("direct");
                return uncertainLayer == "direct" ? InsertionAttempt.UnknownSideEffect("uncertain") : InsertionAttempt.Unsupported();
            },
            paste: () =>
            {
                calls.Add("paste");
                return uncertainLayer == "paste" ? InsertionAttempt.UnknownSideEffect("uncertain") : InsertionAttempt.Failed("failed");
            },
            send: () => { calls.Add("send"); return InsertionAttempt.Inserted(); },
            copy: _ => { calls.Add("copy"); return InsertionAttempt.Inserted(); });

        var result = await service.InsertAsync(SafeTarget(), "text", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.ClipboardOnly, result.Method);
        Assert.Equal(uncertainLayer == "direct"
            ? ["direct", "copy"]
            : ["direct", "paste", "copy"], calls);
    }

    [Theory]
    [MemberData(nameof(RestrictedTargets))]
    public async Task InsertAsync_RestrictionsRecoverWithoutCallingInsertionLayers(ActiveTarget target)
    {
        var insertionCalled = false;
        string? copied = null;
        var service = Service(
            direct: () => { insertionCalled = true; return InsertionAttempt.Inserted(); },
            paste: () => { insertionCalled = true; return InsertionAttempt.Inserted(); },
            send: () => { insertionCalled = true; return InsertionAttempt.Inserted(); },
            copy: text => { copied = text; return InsertionAttempt.Inserted(); });

        var result = await service.InsertAsync(target, "recovery", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.ClipboardOnly, result.Method);
        Assert.False(insertionCalled);
        Assert.Equal("recovery", copied);
    }

    [Fact]
    public async Task InsertAsync_TerminalUsesOnlyPasteAndRetainsClipboard()
    {
        var directCalled = false;
        var sendCalled = false;
        bool? retainClipboard = null;
        var service = new ClipboardTextInsertionService(
            (_, _) => Task.FromResult(true),
            (_, _) => { directCalled = true; return InsertionAttempt.Inserted(); },
            (_, _, retain, _) =>
            {
                retainClipboard = retain;
                return Task.FromResult(InsertionAttempt.Inserted());
            },
            (_, _) => { sendCalled = true; return InsertionAttempt.Inserted(); },
            (_, _) => Task.FromResult(InsertionAttempt.Inserted()));

        var result = await service.InsertAsync(SafeTarget() with { IsTerminal = true }, "text", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TextInsertionMethod.ClipboardPaste, result.Method);
        Assert.True(retainClipboard == true);
        Assert.False(directCalled);
        Assert.False(sendCalled);
    }

    [Fact]
    public async Task InsertAsync_TotalFailureCopiesRecoveryTextAndReportsFalse()
    {
        string? copied = null;
        var service = Service(
            direct: () => InsertionAttempt.Unsupported(),
            paste: () => InsertionAttempt.Failed("paste failed"),
            send: () => InsertionAttempt.Failed("send failed"),
            copy: text => { copied = text; return InsertionAttempt.Inserted(); });

        var result = await service.InsertAsync(SafeTarget(), "keep me", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.ClipboardOnly, result.Method);
        Assert.Equal("keep me", copied);
        Assert.Contains("send failed", result.Error);
    }

    public static TheoryData<ActiveTarget> RestrictedTargets() => new()
    {
        SafeTarget() with { IsPasswordField = true },
        SafeTarget() with { IsPasswordField = null },
        SafeTarget() with { CurrentIntegrityRid = 0x2000, TargetIntegrityRid = 0x3000 },
        SafeTarget() with { CurrentIntegrityRid = null },
        SafeTarget() with { IsInjectionSafe = false }
    };

    private static ClipboardTextInsertionService Service(
        Func<bool>? restore = null,
        Func<InsertionAttempt>? direct = null,
        Func<InsertionAttempt>? paste = null,
        Func<InsertionAttempt>? send = null,
        Func<string, InsertionAttempt>? copy = null) => new(
            (_, _) => Task.FromResult(restore?.Invoke() ?? true),
            (_, _) => direct?.Invoke() ?? InsertionAttempt.Inserted(),
            (_, _, _, _) => Task.FromResult(paste?.Invoke() ?? InsertionAttempt.Inserted()),
            (_, _) => send?.Invoke() ?? InsertionAttempt.Inserted(),
            (text, _) => Task.FromResult(copy?.Invoke(text) ?? InsertionAttempt.Inserted()));

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
