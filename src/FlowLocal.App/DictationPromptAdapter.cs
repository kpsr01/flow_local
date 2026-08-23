using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>
/// Builds prompts in the exact input format S1-mini was trained on
/// (https://huggingface.co/superwhisper/s1-mini-GGUF): the official system
/// prompt verbatim, a control line restricted to the trained values
/// (casual / semi-casual / semi-formal / formal, prose / lists, general / email),
/// and the Qwen3 chat template with thinking off — the assistant turn opens
/// with an empty <think> block. Do not add instructions: extra prompting
/// degrades a model trained on this fixed format.
/// </summary>
internal static class DictationPromptAdapter
{
    internal const string SystemPrompt =
        "You are a text normalizer for speech-to-text transcripts. The input begins with a control line specifying the styling, structure, and context settings; clean the transcript to match those settings and output only the cleaned text.";

    /// <summary>Qwen3 non-thinking assistant prefix: empty think block, two newlines inside and after.</summary>
    internal const string GenerationPrefill = "<|im_start|>assistant\n<think>\n\n</think>\n\n";

    internal static string Build(RawTranscript transcript, TranscriptStyle style) =>
        $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\n[Styling: {Styling(style.Tone)}] [Structure: {(style.EnableLists ? "lists" : "prose")}] [Context: {Context(style.Category)}]\n{transcript.Text}<|im_end|>\n{GenerationPrefill}";

    /// <summary>S1-mini supports only general and email contexts; every other
    /// category maps onto general rather than inventing unsupported tokens.</summary>
    private static string Context(string category) =>
        string.Equals(category.Trim(), "email", StringComparison.OrdinalIgnoreCase) ? "email" : "general";

    private static string Styling(string tone) => tone.Trim().ToLowerInvariant() switch
    {
        "casual" => "casual",
        "semi-casual" or "semicasual" => "semi-casual",
        "formal" => "formal",
        _ => "semi-formal",
    };
}
