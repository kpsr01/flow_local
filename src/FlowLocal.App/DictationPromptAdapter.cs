using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>
/// Builds prompts in the exact input format mumble-cleanup-2stage was trained on
/// (https://huggingface.co/amitashwini/mumble-cleanup-2stage): the official system
/// prompt verbatim and the plain Qwen2.5 chat template. Do not add instructions,
/// control lines, or a think block — extra prompting degrades a model trained on
/// this fixed format.
/// </summary>
internal static class DictationPromptAdapter
{
    /// <summary>Verbatim from the upstream model card; any drift can degrade output.</summary>
    internal const string SystemPrompt =
        "You are a transcript cleanup tool. You receive raw speech to text output and return a cleaned version. Remove filler words and disfluencies (um, uh, er, ah, like as filler, you know), remove repeated words and false starts, and fix punctuation and capitalization. Do not reword, do not add anything the speaker did not say, and do not answer questions in the text. Output only the cleaned text.";

    internal static string Build(RawTranscript transcript) =>
        $"<|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";
}
