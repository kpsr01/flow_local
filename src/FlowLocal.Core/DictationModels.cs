namespace FlowLocal.Core;

public sealed record AsrSessionOptions(Guid SessionId, int SampleRate = 16_000, int BitsPerSample = 16, int Channels = 1);

public sealed record AsrResult(string Text);

public sealed record RawTranscript(string Text);

public sealed record TranscriptStyle(
    string Category,
    string Tone,
    string Structure,
    bool UseStandardCapitalization = true,
    bool EnableParagraphs = true,
    bool EnableLists = false,
    bool EnableEmailFormatting = false,
    bool PreserveTechnicalTokens = true,
    bool UseSmartPunctuation = true);

public enum OutputContextCategory
{
    Email,
    WorkMessaging,
    PersonalMessaging,
    Document,
    AiChat,
    CodeEditor,
    Terminal,
    General
}

public enum ClassificationSource
{
    DomainOverride,
    ExecutableOverride,
    KnownDomain,
    KnownApplication,
    ControlHint,
    GenericBrowser,
    General
}

public sealed record OutputClassification(
    OutputContextCategory Category,
    TranscriptStyle Style,
    ClassificationSource Source,
    string Rule,
    ContextDetectionDiagnostic Diagnostic);

public sealed record OutputStyleOverride(
    OutputContextCategory Category,
    TranscriptStyle Style);

public sealed record OutputStyleSettings(
    bool StyleClassificationEnabled = true,
    bool WebsiteDetectionEnabled = true,
    OutputContextCategory UniversalDefaultCategory = OutputContextCategory.General,
    TranscriptStyle? UniversalDefaultStyle = null,
    IReadOnlyDictionary<string, OutputStyleOverride>? DomainOverrides = null,
    IReadOnlyDictionary<string, OutputStyleOverride>? ExecutableOverrides = null);

public sealed record StyleOverrideLoadResult(
    OutputStyleSettings Settings,
    string? Diagnostic = null);

public sealed record CleanTranscriptResult(string Text);

public sealed record BackendAvailability(bool IsAvailable, string? UnavailableReason = null);

public enum BrowserIdentity
{
    Chrome,
    Edge,
    Firefox
}

public enum ContextDetectionConfidence
{
    None,
    Low,
    High
}

public sealed record ContextDetectionDiagnostic(
    ContextDetectionConfidence Confidence,
    string Source,
    string? Error = null);

public sealed record ApplicationContext(
    string ExecutableName,
    string DisplayName,
    string WindowTitle,
    string? ControlType,
    bool IsBrowser,
    BrowserIdentity? Browser,
    string? Domain,
    ContextDetectionDiagnostic Detection);


public static class CleanupResultValidator
{

    public static bool TryValidate(RawTranscript raw, CleanTranscriptResult cleaned, out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(cleaned);

        var output = cleaned.Text.Trim();
        if (output.Length == 0)
        {
            rejectionReason = "Cleanup output is empty.";
            return false;
        }

        var controlTokenStart = output.IndexOf("<|", StringComparison.Ordinal);
        if (controlTokenStart >= 0 &&
            output.IndexOf("|>", controlTokenStart + 2, StringComparison.Ordinal) >= 0)
        {
            rejectionReason = "Cleanup output contains model control tokens.";
            return false;
        }

        var rawWords = Words(raw.Text);
        var outputWords = Words(output);
        if (rawWords.Count > 0 &&
            (outputWords.Count > (rawWords.Count * 3) + 10 ||
             (rawWords.Count >= 4 && SharedWordRatio(rawWords, outputWords) < 0.25)))
        {
            rejectionReason = "Cleanup output is implausibly expanded or unrelated to the raw transcript.";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private static List<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .ToList();

    private static double SharedWordRatio(List<string> rawWords, List<string> outputWords)
    {
        var rawVocabulary = rawWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputVocabulary = outputWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rawVocabulary.Count(outputVocabulary.Contains) / (double)rawVocabulary.Count;
    }
}

public sealed record ActiveTarget(
    int ProcessId,
    nint WindowHandle,
    string ExecutableName,
    string WindowTitle,
    DateTimeOffset CapturedAt,
    uint WindowThreadId = 0,
    nint FocusedChildWindowHandle = default,
    DateTimeOffset? ProcessStartTime = null,
    string? ExecutablePath = null,
    string WindowClassName = "",
    int? CurrentIntegrityRid = null,
    int? TargetIntegrityRid = null,
    bool IsInjectionSafe = false,
    bool IsTerminal = false,
    string? FocusedAutomationId = null,
    string? FocusedControlType = null,
    string? FocusedName = null,
    bool? IsPasswordField = null);

public enum TextInsertionMethod
{
    Direct,
    ClipboardPaste,
    SendInput,
    ClipboardOnly
}

public sealed record TextInsertionResult(bool Succeeded, TextInsertionMethod Method, string? Error = null);

public enum DictationErrorCode
{
    None,
    AudioCaptureFailed,
    AsrFailed,
    CleanupFailed,
    TargetUnavailable,
    InsertionFailed,
    Cancelled,
    Interrupted
}

public sealed record HistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RecordingStartedAt,
    DateTimeOffset? RecordingEndedAt,
    TimeSpan? Duration,
    string? RawTranscript,
    string? CleanedTranscript,
    string? AudioFilePath,
    string? TargetApplication,
    string? TargetExecutable,
    string? Domain,
    OutputContextCategory? OutputCategory,
    TranscriptStyle? Style,
    string? AsrModelName,
    string? CleanupModelName,
    TimeSpan? AsrDuration,
    TimeSpan? CleanupDuration,
    TimeSpan? InsertionDuration,
    TimeSpan? TotalDuration,
    TextInsertionMethod? InsertionMethod,
    RecordingState State,
    DictationErrorCode ErrorCode = DictationErrorCode.None,
    int RetryCount = 0);

public sealed record HistoryQuery(
    string? Search = null,
    bool FailedOnly = false,
    string? Application = null,
    int? Limit = null,
    int? Offset = null);

public sealed record HistoryRetentionSettings(
    bool SaveAudio = true,
    int AudioRetentionDays = 7,
    int TranscriptRetentionDays = 30);
