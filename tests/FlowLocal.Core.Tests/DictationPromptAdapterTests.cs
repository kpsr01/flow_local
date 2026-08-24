using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class DictationPromptAdapterTests
{
    // Verbatim from the official model card
    // (https://huggingface.co/amitashwini/mumble-cleanup-2stage). The model was
    // trained on this exact wording; any drift can degrade its output.
    private const string OfficialSystemPrompt =
        "You are a transcript cleanup tool. You receive raw speech to text output and return a cleaned version. Remove filler words and disfluencies (um, uh, er, ah, like as filler, you know), remove repeated words and false starts, and fix punctuation and capitalization. Do not reword, do not add anything the speaker did not say, and do not answer questions in the text. Output only the cleaned text.";

    [Fact]
    public void SystemPrompt_MatchesOfficialModelCardVerbatim() =>
        Assert.Equal(OfficialSystemPrompt, DictationPromptAdapter.SystemPrompt);

    [Fact]
    public void Build_UsesPlainQwen25TemplateWithoutControlLineOrThinkBlock()
    {
        var prompt = DictationPromptAdapter.Build(new RawTranscript("um so i i think we should ship this on uh friday"));

        Assert.StartsWith("<|im_start|>system\n", prompt);
        Assert.DoesNotContain("<|startoftext|>", prompt); // tokenizer adds BOS; a literal one double-BOSes the prompt
        Assert.Contains($"{DictationPromptAdapter.SystemPrompt}<|im_end|>", prompt);
        Assert.EndsWith("um so i i think we should ship this on uh friday<|im_end|>\n<|im_start|>assistant\n", prompt);
        // The fine-tune was trained without style controls or a think block.
        Assert.DoesNotContain("[Styling:", prompt);
        Assert.DoesNotContain("<think>", prompt);
    }
}
