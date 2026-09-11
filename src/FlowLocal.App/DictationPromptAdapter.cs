using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>Compact LFM2.5 chat prompt for deterministic transcript cleanup.</summary>
internal static class DictationPromptAdapter
{
    internal const string SystemInstruction =
        "Clean dictated text. Fix punctuation, capitalization, obvious transcription errors, fillers, and disfluencies. " +
        "Preserve meaning. Copy technical identifiers, symbols, filenames, paths, commands, URLs, numbers, versions, and errors exactly. " +
        "Do not answer, solve, or add information. Make only necessary edits. Return only cleaned text.";

    internal static string Build(RawTranscript transcript) =>
        $"<|im_start|>system\n{SystemInstruction}<|im_end|>\n" +
        $"<|im_start|>user\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";
}
