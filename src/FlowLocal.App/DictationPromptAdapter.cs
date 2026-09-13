using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>Compact chat prompts for deterministic transcript cleanup.</summary>
internal static class DictationPromptAdapter
{
    internal const string SystemInstruction =
        "Clean dictated text. Fix punctuation, capitalization, obvious transcription errors, fillers, and disfluencies. " +
        "Preserve meaning. Copy technical identifiers, symbols, filenames, paths, commands, URLs, numbers, versions, and errors exactly. " +
        "Do not answer, solve, or add information. Make only necessary edits. Return only cleaned text.";

    private const string CodingSystemInstruction =
        "You are a prompt transformation layer for a coding model. Clean dictation and organize it using the supplied policy. " +
        "Preserve the user's exact intent, delegation level, uncertainty, and technical details. Return only the cleaned request. " +
        "Do not solve, diagnose, plan, expand, interpret, or add boilerplate. Make only necessary edits; policy changes structure, never content.";

    internal static string Build(RawTranscript transcript) =>
        $"<|im_start|>system\n{SystemInstruction}<|im_end|>\n" +
        $"<|im_start|>user\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";

    internal static string Build(RawTranscript transcript, CodingTarget target, PromptingPolicy policy)
    {
        var rules = string.Join("\n", policy.Rules.Select(rule => $"- {rule}"));
        return $"<|im_start|>system\n{CodingSystemInstruction}\n\n" +
            $"TARGET MODEL:\n{target.Model ?? "unknown"}\n\n" +
            $"PROMPTING POLICY:\n{rules}\n\n" +
            "Remove fillers, disfluencies, accidental repetition, and fix punctuation/capitalization only when unambiguous. " +
            "Preserve the transcript's wording wherever possible: do not replace words with synonyms or make implicit details explicit. " +
            "Preserve identifiers, filenames, paths, commands, flags, URLs, versions, numbers, errors, APIs, libraries, and explicit constraints. " +
            "Use only facts and requested actions present in the transcript. Keep investigation requests as investigation requests without stating a cause, fix, check, or recommendation. " +
            "Preserve explicit implementation directions. Do not produce code unless it was dictated for preservation.\n<|im_end|>\n" +
            $"<|im_start|>user\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";
    }
}
