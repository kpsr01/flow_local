using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class DictationPromptAdapterTests
{
    // Verbatim from the official model card
    // (https://huggingface.co/superwhisper/s1-mini-GGUF). S1-mini was trained on
    // this exact wording; any drift can make it hallucinate or garble output.
    private const string OfficialSystemPrompt =
        "You are a text normalizer for speech-to-text transcripts. The input begins with a control line specifying the styling, structure, and context settings; clean the transcript to match those settings and output only the cleaned text.";

    [Fact]
    public void SystemPrompt_MatchesOfficialModelCardVerbatim() =>
        Assert.Equal(OfficialSystemPrompt, DictationPromptAdapter.SystemPrompt);

    [Fact]
    public void Build_UsesQwen3TemplateWithControlLineAndEmptyThinkBlock()
    {
        var prompt = DictationPromptAdapter.Build(
            new RawTranscript("um send it friday no sorry thursday"),
            new TranscriptStyle("Email", "formal", "Prose", EnableLists: false, EnableEmailFormatting: true));

        Assert.StartsWith("<|im_start|>system\n", prompt);
        Assert.DoesNotContain("<|startoftext|>", prompt); // tokenizer adds BOS; a literal one double-BOSes the prompt
        Assert.Contains($"{DictationPromptAdapter.SystemPrompt}<|im_end|>", prompt);
        Assert.Contains("<|im_start|>user\n[Styling: formal] [Structure: prose] [Context: email]\n", prompt);
        // Non-thinking assistant prefix: empty think block, exactly as trained.
        Assert.EndsWith("um send it friday no sorry thursday<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);
    }

    [Fact]
    public void Build_GeneralDefaultUsesSemiFormalListsGeneralControlLine()
    {
        var prompt = DictationPromptAdapter.Build(
            new RawTranscript("one two three"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.General));

        Assert.Contains("[Styling: semi-formal] [Structure: lists] [Context: general]", prompt);
    }

    [Fact]
    public void Build_MapsOnlyTrainedControlValues()
    {
        var prompt = DictationPromptAdapter.Build(
            new RawTranscript("one two three"),
            new TranscriptStyle("Unknown", "unsupported", "Unknown", EnableLists: true));

        Assert.Contains("[Styling: semi-formal] [Structure: lists] [Context: general]", prompt);
    }

    [Fact]
    public void Build_MapsResolvedStylesOntoTrainedAxisValues()
    {
        Assert.Contains("[Styling: casual]", DictationPromptAdapter.Build(
            new RawTranscript("hey"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.PersonalMessaging)));
        Assert.Contains("[Styling: semi-casual]", DictationPromptAdapter.Build(
            new RawTranscript("hey"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.WorkMessaging)));
        Assert.Contains("[Context: email]", DictationPromptAdapter.Build(
            new RawTranscript("hey sarah"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.Email)));
        Assert.Contains("[Structure: prose]", DictationPromptAdapter.Build(
            new RawTranscript("x"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal)));

    }
}
