using System.Text.RegularExpressions;

namespace FlowLocal.Core;

/// <summary>Bounded speech-to-request edits shared by decoding and validation.</summary>
public static class CodingRequestNormalizer
{
    // Only unwrap an explicit request before an action. Capability questions without
    // these action verbs and uncertainty in the body remain untouched.
    private const string Actions = "fix|add|remove|update|change|refactor|implement|create|build|write|test|check|inspect|investigate|review|explain|find|compare|summarize|show|help";
    private static readonly Regex RequestLead = new(
        @"\A(?:(?:uh|um|er|erm|basically)[,\s]+)*(?:(?:can|could|would) you (?:please )?|please |i (?:want|need) you to )(?=(?:" + Actions + @")\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RawTranscript Normalize(RawTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var text = transcript.Text.Trim();
        // A question mark can signal a real question, rather than a polite imperative.
        if (!text.EndsWith('?')) text = RequestLead.Replace(text, "", 1);
        return new RawTranscript(text);
    }
}
