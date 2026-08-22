using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class DictationPromptAdapterTests
{
    [Fact]
    public void Build_UsesLfmTemplateWithControlLineAndGenerationPrefill()
    {
        var prompt = DictationPromptAdapter.Build(
            new RawTranscript("um send it friday no sorry thursday"),
            new TranscriptStyle("Email", "Formal", "Prose", EnableEmailFormatting: true));

        Assert.StartsWith("<|startoftext|><|im_start|>system\n", prompt);
        Assert.Contains($"{DictationPromptAdapter.SystemPrompt}<|im_end|>", prompt);
        Assert.Contains("<|im_start|>user\n[Styling: formal] [Structure: prose] [Context: email]\n", prompt);
        Assert.EndsWith("um send it friday no sorry thursday<|im_end|>\n<|im_start|>assistant\n", prompt);
        Assert.DoesNotContain("<think>", prompt);
    }

    [Fact]
    public void Build_MapsOnlyDocumentedControlValues()
    {
        var prompt = DictationPromptAdapter.Build(
            new RawTranscript("one two three"),
            new TranscriptStyle("Unknown", "unsupported", "Unknown", EnableLists: true));

        Assert.Contains("[Styling: semi-formal] [Structure: lists] [Context: general]", prompt);
    }

    [Fact]
    public void SystemPrompt_CoversFillersFinalIntentPunctuationListsAndOutputContract()
    {
        var prompt = DictationPromptAdapter.SystemPrompt;

        Assert.Contains("dictation transcripts into polished written text", prompt);
        Assert.Contains("never answer, reply, or add information", prompt);
        Assert.Contains("fillers and disfluencies", prompt);
        Assert.Contains("final intent", prompt);
        Assert.Contains("punctuation and capitalization", prompt);
        Assert.Contains("numbered lists", prompt);
        Assert.Contains("Output only the cleaned text", prompt);
    }
}
