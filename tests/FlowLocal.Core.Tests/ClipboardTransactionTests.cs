using System.Windows;
using System.Windows.Threading;
using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class ClipboardTransactionTests
{
    [Fact]
    public Task RestoreAsync_DoesNotOverwriteClipboardChangedAfterStaging() => RunOnStaAsync(async () =>
    {
        uint sequence = 10;
        var backup = new DataObject(DataFormats.UnicodeText, "before");
        IDataObject? restored = null;
        var clipboard = new ClipboardTransaction(
            dispatcher: Dispatcher.CurrentDispatcher,
            sequenceNumber: () => sequence,
            getData: () => backup,
            setText: _ => sequence = 11,
            restoreData: data => restored = data);

        var stage = await clipboard.StageAsync("dictated", CancellationToken.None);
        sequence = 12;
        await clipboard.RestoreAsync(stage, TimeSpan.Zero, CancellationToken.None);

        Assert.Null(restored);
    });

    [Fact]
    public Task TerminalPaste_LeavesTranscriptOnClipboard() => RunOnStaAsync(async () =>
    {
        uint sequence = 20;
        string? clipboardText = "before";
        var restoreCalls = 0;
        var clipboard = new ClipboardTransaction(
            dispatcher: Dispatcher.CurrentDispatcher,
            sequenceNumber: () => sequence,
            getData: () => new DataObject(DataFormats.UnicodeText, clipboardText),
            setText: text =>
            {
                clipboardText = text;
                sequence++;
            },
            restoreData: _ => restoreCalls++);
        var paste = new ClipboardPasteInsertion(clipboard, () => true);

        var result = await paste.InsertAsync(TerminalTarget(), "transcript", retainClipboard: true, CancellationToken.None);

        Assert.Equal(InsertionDisposition.Inserted, result.Disposition);
        Assert.Equal("transcript", clipboardText);
        Assert.Equal(0, restoreCalls);
    });

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = action().ContinueWith(task =>
            {
                if (task.IsCanceled)
                    completion.SetCanceled();
                else if (task.Exception is not null)
                    completion.SetException(task.Exception.InnerExceptions);
                else
                    completion.SetResult();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }, TaskScheduler.Default);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ActiveTarget TerminalTarget() => new(
        42,
        (nint)123,
        "WindowsTerminal",
        "Terminal",
        DateTimeOffset.UnixEpoch,
        CurrentIntegrityRid: 0x2000,
        TargetIntegrityRid: 0x2000,
        IsInjectionSafe: true,
        IsTerminal: true,
        IsPasswordField: false);
}
