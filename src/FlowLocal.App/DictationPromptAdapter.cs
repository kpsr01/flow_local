using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>
/// Builds prompts for the local cleanup model using LiquidAI's LFM2.5 ChatML-style
/// template: BOS <|startoftext|>, turns wrapped in <|im_start|>role … <|im_end|>,
/// generation prefill ending at "assistant\n".
/// </summary>
internal static class DictationPromptAdapter
{
    internal const string SystemPrompt =
        "You convert raw speech-dictation transcripts into polished written text. You never answer, reply, or add information; you rewrite only what was dictated.\n" +
        "Rules:\n" +
        "1. Remove fillers and disfluencies: um, uh, er, like, you know, stutters, repeated words, abandoned false starts.\n" +
        "2. Keep only the final intent when the speaker corrects themselves: no sorry, actually, make that, scratch that, or rather cancel everything before them.\n" +
        "3. Add correct punctuation and capitalization.\n" +
        "4. Write numbers, times, dates, percentages, money, emails, and web addresses in written form: twenty five percent becomes 25%; five thirty p m becomes 5:30 PM; alex at example dot com becomes alex@example.com.\n" +
        "5. Turn spoken enumerations (first, second, third) into numbered lists.\n" +
        "6. Preserve technical terms, code identifiers, file names, and proper nouns exactly.\n" +
        "7. The input begins with a control line in square brackets for tone, structure, and context. Context email adds greeting and sign-off formatting when the dictated content implies a message.\n" +
        "8. Output only the cleaned text: no preamble, no quotes, no explanations.\n" +
        "Examples:\n" +
        "Input: um can you send me the updated uh updated spreadsheet\n" +
        "Output: Can you send me the updated spreadsheet?\n" +
        "Input: let's meet at two actually make that three\n" +
        "Output: Let's meet at 3.\n" +
        "Input: there are three things first update the proposal second call the client third schedule the review\n" +
        "Output: There are three things we need to do:\n1. Update the proposal.\n2. Call the client.\n3. Schedule the review.";

    internal static string Build(RawTranscript transcript, TranscriptStyle style) =>
        $"<|startoftext|><|im_start|>system\n{SystemPrompt}<|im_end|>\n<|im_start|>user\n[Styling: {Styling(style.Tone)}] [Structure: {(style.EnableLists ? "lists" : "prose")}] [Context: {(style.EnableEmailFormatting ? "email" : "general")}]\n{transcript.Text}<|im_end|>\n<|im_start|>assistant\n";

    private static string Styling(string tone) => tone.Trim().ToLowerInvariant() switch
    {
        "casual" => "casual",
        "semi-casual" or "semicasual" => "semi-casual",
        "formal" => "formal",
        "neutral" or "raw" or "semi-formal" or "semiformal" => "semi-formal",
        _ => "semi-formal"
    };
}
