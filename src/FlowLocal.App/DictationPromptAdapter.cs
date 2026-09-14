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
        "Format this dictated coding request, not an answer. Copy words in order; remove leading fillers only. " +
        "Preserve technical tokens exactly. Separate distinct requirements with paragraphs or bullets. " +
        "For mixed requests, label existing content Task:, Context:, Constraints:, Steps:, or Output:. " +
        "Context means background; Constraints means must, do not, only, or keep clauses. " +
        "Steps means dictated actions; Output means requested deliverables. Keep simple requests unlabelled. " +
        "Never invent content. Return only the formatted request.";

    internal static string Build(RawTranscript transcript) =>
        $"<|im_start|>system\n{SystemInstruction}<|im_end|>\n" +
        $"<|im_start|>user\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";

    internal static string Build(RawTranscript transcript, PromptingPolicy policy)
    {
        return $"<|im_start|>system\n{CodingSystemInstruction}\n<policy>\n" +
            $"{string.Join(" ", policy.Rules)}\n</policy><|im_end|>\n" +
            $"<|im_start|>user\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";
    }
}
