using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace FlowLocal.App;

internal sealed class ClipboardTransaction
{
    private const int RetryCount = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private readonly Dispatcher _dispatcher;
    private readonly Func<uint> _sequenceNumber;
    private readonly Func<IDataObject?> _getData;
    private readonly Action<string> _setText;
    private readonly Action<IDataObject> _restoreData;

    internal ClipboardTransaction(
        Dispatcher? dispatcher = null,
        Func<uint>? sequenceNumber = null,
        Func<IDataObject?>? getData = null,
        Action<string>? setText = null,
        Action<IDataObject>? restoreData = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _sequenceNumber = sequenceNumber ?? GetClipboardSequenceNumber;
        _getData = getData ?? Clipboard.GetDataObject;
        _setText = setText ?? (text => Clipboard.SetText(text, TextDataFormat.UnicodeText));
        _restoreData = restoreData ?? (data => Clipboard.SetDataObject(data, true));
    }

    internal async Task<ClipboardStage> StageAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        IDataObject? backup;
        try
        {
            backup = await RetryAsync(_getData, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is COMException or ExternalException)
        {
            backup = null;
        }

        await RetryAsync(() => _setText(text), cancellationToken).ConfigureAwait(false);
        return new ClipboardStage(backup, _sequenceNumber());
    }

    internal async Task RestoreAsync(ClipboardStage stage, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (stage.Backup is null)
            return;

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        if (_sequenceNumber() != stage.StagedSequenceNumber)
            return;

        await RetryAsync(() =>
        {
            if (_sequenceNumber() == stage.StagedSequenceNumber)
                _restoreData(stage.Backup);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RetryAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken);
            }
            catch (Exception exception) when (IsClipboardContention(exception) && attempt < RetryCount - 1)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RetryAsync(Action action, CancellationToken cancellationToken) =>
        await RetryAsync(() =>
        {
            action();
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private static bool IsClipboardContention(Exception exception) =>
        exception is COMException or ExternalException;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}

internal sealed record ClipboardStage(IDataObject? Backup, uint StagedSequenceNumber);
