using System.Windows;
using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class ClipboardTextInsertionService : ITextInsertionService
{
    private readonly Func<ActiveTarget, CancellationToken, Task<bool>> _restoreTarget;
    private readonly Func<ActiveTarget, string, InsertionAttempt> _uiAutomation;
    private readonly Func<ActiveTarget, string, bool, CancellationToken, Task<InsertionAttempt>> _paste;
    private readonly Func<ActiveTarget, string, InsertionAttempt> _sendInput;
    private readonly Func<string, CancellationToken, Task<InsertionAttempt>> _copyOnly;

    public ClipboardTextInsertionService()
    {
        var targets = new ActiveTargetTracker();
        var clipboard = new ClipboardPasteInsertion();
        var sendInput = new SendInputInsertion();
        _restoreTarget = targets.RestoreAndValidateAsync;
        _uiAutomation = UiAutomationInsertion.TryInsert;
        _paste = clipboard.InsertAsync;
        _sendInput = sendInput.TryInsert;
        _copyOnly = clipboard.CopyOnlyAsync;
    }

    internal ClipboardTextInsertionService(
        Func<ActiveTarget, CancellationToken, Task<bool>> restoreTarget,
        Func<ActiveTarget, string, InsertionAttempt> uiAutomation,
        Func<ActiveTarget, string, bool, CancellationToken, Task<InsertionAttempt>> paste,
        Func<ActiveTarget, string, InsertionAttempt> sendInput,
        Func<string, CancellationToken, Task<InsertionAttempt>> copyOnly)
    {
        _restoreTarget = restoreTarget;
        _uiAutomation = uiAutomation;
        _paste = paste;
        _sendInput = sendInput;
        _copyOnly = copyOnly;
    }

    public async Task<TextInsertionResult> InsertAsync(
        ActiveTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(text);
        cancellationToken.ThrowIfCancellationRequested();

        if (target.IsPasswordField != false)
            return await RecoverAsync(text, "Text was not inserted because the focused field is protected or unknown.", cancellationToken);
        if (!target.IsInjectionSafe ||
            !TargetPolicy.IsInjectionSafe(target.CurrentIntegrityRid, target.TargetIntegrityRid) ||
            target.WindowHandle == 0 || target.ProcessId <= 0 ||
            !await _restoreTarget(target, cancellationToken).ConfigureAwait(false))
        {
            return await RecoverAsync(text, "Text was not inserted because the target window is no longer active or cannot be injected safely.", cancellationToken);
        }

        if (!target.IsTerminal)
        {
            var direct = _uiAutomation(target, text);
            if (direct.Disposition == InsertionDisposition.Inserted)
                return new TextInsertionResult(true, TextInsertionMethod.Direct);
            if (direct.Disposition == InsertionDisposition.UnknownSideEffect)
                return await RecoverAsync(text, RecoveryMessage(direct), cancellationToken);
        }

        var paste = await _paste(target, text, target.IsTerminal, cancellationToken).ConfigureAwait(false);
        if (paste.Disposition == InsertionDisposition.Inserted)
            return new TextInsertionResult(true, TextInsertionMethod.ClipboardPaste);
        if (target.IsTerminal || paste.Disposition == InsertionDisposition.UnknownSideEffect)
            return await RecoverAsync(text, RecoveryMessage(paste), cancellationToken);

        var unicode = _sendInput(target, text);
        if (unicode.Disposition == InsertionDisposition.Inserted)
            return new TextInsertionResult(true, TextInsertionMethod.SendInput);

        return await RecoverAsync(text, RecoveryMessage(unicode), cancellationToken);
    }

    private async Task<TextInsertionResult> RecoverAsync(
        string text,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var recovery = await _copyOnly(text, cancellationToken).ConfigureAwait(false);
            if (recovery.Disposition != InsertionDisposition.Inserted)
                Clipboard.SetText(text);
            return new TextInsertionResult(false, TextInsertionMethod.ClipboardOnly, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TextInsertionResult(false, TextInsertionMethod.ClipboardOnly,
                $"{message} Clipboard recovery also failed: {exception.Message}");
        }
    }

    private static string RecoveryMessage(InsertionAttempt attempt) =>
        string.IsNullOrWhiteSpace(attempt.Error)
            ? "Text could not be inserted. It remains on the clipboard for manual paste."
            : $"Text could not be inserted: {attempt.Error} It remains on the clipboard for manual paste.";
}
