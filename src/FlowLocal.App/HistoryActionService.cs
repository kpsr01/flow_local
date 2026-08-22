using System.Diagnostics;
using System.IO;
using System.Windows;
using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class HistoryActionService(
    IHistoryRepository history,
    IAsrService asr,
    ITranscriptCleaner cleaner,
    IActiveTargetTracker targets,
    ITextInsertionService insertion)
{
    public async Task ExecuteAsync(HistoryAction action, HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        switch (action)
        {
            case HistoryAction.RetryAsr: await RetryAsrAsync(entry, cancellationToken); break;
            case HistoryAction.RetryCleanup: await RetryCleanupAsync(entry, cancellationToken); break;
            case HistoryAction.RetryInsertion: await RetryInsertionAsync(entry, cancellationToken); break;
            case HistoryAction.CopyRaw: Copy(entry.RawTranscript); break;
            case HistoryAction.CopyCleaned: Copy(entry.CleanedTranscript); break;
            case HistoryAction.Paste: await PasteAsync(entry, cancellationToken); break;
            case HistoryAction.Play: Play(entry); break;
            case HistoryAction.Export: Export(entry); break;
            case HistoryAction.OpenLocation: OpenLocation(entry); break;
            case HistoryAction.Delete: await history.DeleteAsync(entry.Id, true, cancellationToken); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    public async Task RetryAsrAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var path = Require(entry.AudioFilePath, "This session has no saved audio.");
        var started = Stopwatch.GetTimestamp();
        var result = await AsrRetryService.RetryAsync(asr, path, new AsrSessionOptions(entry.Id), cancellationToken);
        var raw = Require(result.Text, "Speech recognition returned an empty transcript.");
        await history.UpdateAsync(entry with
        {
            RawTranscript = raw,
            CleanedTranscript = null,
            AsrDuration = Stopwatch.GetElapsedTime(started),
            CleanupDuration = null,
            InsertionDuration = null,
            TotalDuration = null,
            InsertionMethod = null,
            RetryCount = entry.RetryCount + 1,
            State = RecordingState.Cleaning,
            ErrorCode = DictationErrorCode.None
        }, cancellationToken);
    }

    public async Task RetryCleanupAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var raw = new RawTranscript(Require(entry.RawTranscript, "This session has no raw transcript."));
        var started = Stopwatch.GetTimestamp();
        var cleaned = await cleaner.CleanAsync(raw, entry.Style ?? TranscriptStyleResolver.Resolve(entry.OutputCategory ?? OutputContextCategory.General), cancellationToken);
        if (!CleanupResultValidator.TryValidate(raw, cleaned, out var reason))
            throw new InvalidOperationException(reason);
        await history.UpdateAsync(entry with
        {
            CleanedTranscript = cleaned.Text,
            CleanupDuration = Stopwatch.GetElapsedTime(started),
            InsertionDuration = null,
            TotalDuration = null,
            InsertionMethod = null,
            RetryCount = entry.RetryCount + 1,
            State = RecordingState.Inserting,
            ErrorCode = DictationErrorCode.None
        }, cancellationToken);
    }

    public async Task RetryInsertionAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var text = entry.CleanedTranscript ?? Require(entry.RawTranscript, "This session has no transcript.");
        var target = await targets.CaptureAsync(cancellationToken);
        if (!await targets.RestoreAndValidateAsync(target, cancellationToken))
        {
            await history.UpdateAsync(entry with
            {
                RetryCount = entry.RetryCount + 1,
                State = RecordingState.Failed,
                ErrorCode = DictationErrorCode.TargetUnavailable
            }, CancellationToken.None);
            throw new InvalidOperationException("The newly selected insertion target is no longer available.");
        }

        var started = Stopwatch.GetTimestamp();
        TextInsertionResult result;
        try
        {
            result = await insertion.InsertAsync(target, text, cancellationToken);
        }
        catch
        {
            await history.UpdateAsync(entry with
            {
                InsertionDuration = Stopwatch.GetElapsedTime(started),
                InsertionMethod = null,
                RetryCount = entry.RetryCount + 1,
                State = RecordingState.Failed,
                ErrorCode = DictationErrorCode.InsertionFailed
            }, CancellationToken.None);
            throw;
        }

        var succeeded = result.Succeeded && result.Method != TextInsertionMethod.ClipboardOnly;
        await history.UpdateAsync(entry with
        {
            InsertionDuration = Stopwatch.GetElapsedTime(started),
            InsertionMethod = result.Method,
            RetryCount = entry.RetryCount + 1,
            State = succeeded ? RecordingState.Completed : RecordingState.Failed,
            ErrorCode = succeeded ? DictationErrorCode.None : DictationErrorCode.InsertionFailed
        }, succeeded ? cancellationToken : CancellationToken.None);
        if (!succeeded)
            throw new InvalidOperationException(result.Error ?? "Text was copied but not inserted.");
    }

    public Task PasteAsync(HistoryEntry entry, CancellationToken cancellationToken = default) => RetryInsertionAsync(entry, cancellationToken);
    public void CopyRaw(HistoryEntry entry) => Copy(entry.RawTranscript);
    public void CopyCleaned(HistoryEntry entry) => Copy(entry.CleanedTranscript);
    public void Play(HistoryEntry entry) => Start(Require(entry.AudioFilePath, "This session has no saved audio."));
    public void OpenLocation(HistoryEntry entry) => Start("explorer.exe", $"/select,\"{Require(entry.AudioFilePath, "This session has no saved audio.")}\"");

    public void Export(HistoryEntry entry)
    {
        var source = Require(entry.AudioFilePath, "This session has no saved audio.");
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(source), Filter = "Wave audio (*.wav)|*.wav" };
        if (dialog.ShowDialog() == true) File.Copy(source, dialog.FileName, overwrite: true);
    }

    public Task DeleteAsync(HistoryEntry entry, CancellationToken cancellationToken = default) => history.DeleteAsync(entry.Id, true, cancellationToken);

    private static void Copy(string? text) => Clipboard.SetText(Require(text, "This transcript is unavailable."));
    private static string Require(string? value, string message) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
    private static void Start(string fileName, string? arguments = null) => Process.Start(new ProcessStartInfo(fileName, arguments ?? "") { UseShellExecute = true });
}
