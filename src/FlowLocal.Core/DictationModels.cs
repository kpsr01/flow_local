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

public sealed record CodingTarget(
    string Environment,
    string? Model,
    string? Reasoning,
    string SignalSource,
    string? UnknownReason = null,
    string? Harness = null)
{
    public bool IsKnown => !string.IsNullOrWhiteSpace(Model);
}

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
public static class CodingCleanupValidator
{
    private static readonly HashSet<string> SafeFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "uh", "um", "er", "erm", "basically"
    };

    private static readonly string[] SectionLabels =
        ["Task:", "Context:", "Constraints:", "Requirements:", "Steps:", "Output:"];

    public static bool PreservesSubstantiveWords(RawTranscript raw, CleanTranscriptResult cleaned)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(cleaned);

        return PreservesWords(raw, cleaned) || PreservesWords(CodingRequestNormalizer.Normalize(raw), cleaned);
    }

    private static bool PreservesWords(RawTranscript raw, CleanTranscriptResult cleaned)
    {
        var source = raw.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sourceIndex = 0;
        var leadingFillersEnd = 0;
        while (leadingFillersEnd < source.Length && IsSafeFiller(source[leadingFillersEnd])) leadingFillersEnd++;
        foreach (var line in cleaned.Text.Split('\n'))
        {
            var output = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Labels are formatting, but a dictated label must still be consumed.
            if (output.Length == 1 && SectionLabels.Contains(output[0], StringComparer.Ordinal) &&
                (sourceIndex >= source.Length || source[sourceIndex] != output[0]))
                continue;
            for (var i = 0; i < output.Length; i++)
            {
                var token = output[i];
                if (i == 0 && output.Length > 1 && sourceIndex < source.Length &&
                    token != source[sourceIndex] && IsListMarker(token))
                    continue;
                while (sourceIndex < leadingFillersEnd && !Equivalent(source[sourceIndex], token, false)) sourceIndex++;
                if (sourceIndex >= source.Length || !Equivalent(source[sourceIndex], token,
                    sourceIndex == source.Length - 1))
                    return false;
                sourceIndex++;
            }
        }

        return sourceIndex == source.Length;
    }


    public static string CreateFormattingGrammar(RawTranscript transcript)
    {
        transcript = CodingRequestNormalizer.Normalize(transcript);
        var words = transcript.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var structured = words.Length >= 8;
        var grammar = new System.Text.StringBuilder(structured ? "root ::= task? " : "root ::= ");
        var leading = true;
        string? lastHeading = null;
        var anchorClauses = CanAnchorClauses(words);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var literal = System.Text.Json.JsonSerializer.Serialize(word);
            if (leading && IsSafeFiller(word) && i < words.Length - 1)
            {
                grammar.Append('(').Append(literal).Append(" sep)? ");
                continue;
            }
            leading = false;
            grammar.Append(literal);
            if (word.All(char.IsLetter)) grammar.Append(" punct?");
            else if (i == words.Length - 1) grammar.Append(" \".\"?");
            if (i < words.Length - 1)
            {
                var heading = structured ? HeadingBefore(words, i + 1) : null;
                if (heading is null) grammar.Append(" sep ");
                else if (anchorClauses)
                {
                    // Unambiguous imperative requests get real clause boundaries even
                    // when the small cleanup model prefers to copy one long paragraph.
                    if (heading == lastHeading) grammar.Append(" \"\\n\" ");
                    else grammar.Append(" \"\\n\\n\" ")
                        .Append(heading.TrimEnd(':').ToLowerInvariant()).Append(' ');
                    lastHeading = heading;
                }
                else grammar.Append(" (sep | \"\\n\\n\" ")
                    .Append(heading.TrimEnd(':').ToLowerInvariant())
                    .Append(") ");
            }
        }
        if (structured)
            grammar.Append("\ntask ::= \"Task:\\n\"\n")
                .Append("constraints ::= \"Constraints:\\n\"\noutput ::= \"Output:\\n\"\nsteps ::= \"Steps:\\n\"\ncontext ::= \"Context:\\n\"\n")
                .Append("sep ::= \" \" | \"\\n\" | \"\\n\\n\" | \"\\n- \"\npunct ::= [.,;:!?]\n");
        else
            grammar.Append("\nsep ::= \" \" | \"\\n\" | \"\\n\\n\" | \"\\n- \"\npunct ::= [.,;:!?]\n");
        return grammar.ToString();
    }

    // Restrict generated labels to plausible clause starts. A heading must never be
    // inserted mid-identifier or arbitrarily assign a deliverable to a plan section.
    private static string? HeadingBefore(string[] words, int index)
    {
        if (index < 3) return null;
        // Do not split a verb away from its negation, infinitive, or modal:
        // "do not return a value" is one constraint, not a requested output.
        if (words[index - 1].ToLowerInvariant() is "not" or "never" or "don't" or
            "cannot" or "can't" or "to" or "without" or "must" or "should" or
            "will" or "would" or "could" or "can" or "ever") return null;
        var word = words[index].ToLowerInvariant();
        var next = index + 1 < words.Length ? words[index + 1].ToLowerInvariant() : "";
        return word switch
        {
            "keep" or "without" => "Constraints:",
            "do" when next == "not" => "Constraints:",
            "don't" => "Constraints:",
            "return" when next is "a" or "the" or "only" => "Output:",
            "summarize" => "Output:",
            "explain" when next is "the" or "what" or "why" => "Output:",
            "then" or "next" or "finally" when next is "fix" or "add" or "update" or "run" or
                "test" or "check" or "inspect" or "review" or "implement" or "remove" => "Steps:",
            "because" => "Context:",
            _ => null
        };
    }

    private static bool CanAnchorClauses(string[] words)
    {
        var first = words.FirstOrDefault(word => !IsSafeFiller(word))?.ToLowerInvariant();
        if (first is not ("fix" or "add" or "update" or "refactor" or "implement" or
            "create" or "build" or "inspect" or "review" or "investigate")) return false;
        // Quoted/code content and subordinate clauses need the model's judgment:
        // "fix X if Y", "investigate why ...", etc. must not acquire new scope.
        return !words.Any(word => word.IndexOfAny(['"', '\'', '`']) >= 0 ||
            word.Trim(',', '.', ';', ':', '!', '?').ToLowerInvariant() is
                "if" or "unless" or "whether" or "why" or "how" or "when" or "where" or "that" or "because");
    }
    private static bool IsSafeFiller(string token) =>
        SafeFillers.Contains(token.Trim(',', '.', ';', ':', '!', '?'));

    private static bool IsListMarker(string token) => token is "-" or "*" or "+" ||
        token.Length > 1 && token[^1] is '.' or ')' && token[..^1].All(char.IsAsciiDigit);

    private static bool Equivalent(string raw, string output, bool last)
    {
        if (string.Equals(raw, output, StringComparison.Ordinal)) return true;
        if (last && output.EndsWith('.') && string.Equals(raw, output[..^1], StringComparison.Ordinal))
            return true;
        // Only natural words may gain punctuation; technical tokens retain exact spelling.
        return raw.All(char.IsLetter) &&
            string.Equals(raw, output.TrimEnd(',', '.', ';', ':', '!', '?'), StringComparison.Ordinal);
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
    int RetryCount = 0,
    CodingTarget? CodingTarget = null);

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
