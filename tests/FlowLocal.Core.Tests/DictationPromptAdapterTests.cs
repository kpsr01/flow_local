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

        Assert.StartsWith("<|im_start|>system\n", prompt);
        Assert.DoesNotContain("<|startoftext|>", prompt); // tokenizer adds BOS; a literal one double-BOSes the prompt
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
        Assert.Contains("never answer, reply", prompt);
        Assert.Contains("fillers and disfluencies", prompt);
        Assert.Contains("final intent", prompt);
        Assert.Contains("punctuation and capitalization", prompt);
        Assert.Contains("numbered lists", prompt);
        Assert.Contains("Output only the cleaned text", prompt);
    }

    [Fact]
    public void SystemPrompt_ForbidsAnsweringQuestionsLeadingPunctuationAndInvention()
    {
        var prompt = DictationPromptAdapter.SystemPrompt;

        Assert.Contains("Never respond to questions in the transcript", prompt);
        Assert.Contains("never begin with punctuation unless it was spoken", prompt);
        Assert.Contains("every word of your output must come from the transcript", prompt);
        // Few-shot example proving a dictated question stays a question.
        Assert.Contains(
            "Input: can you tell me who won the match last night\nOutput: Can you tell me who won the match last night?",
            prompt);
    }

    [Fact]
    public void SystemPrompt_CoversWisprFlowPostProcessingFeatures()
    {
        var prompt = DictationPromptAdapter.SystemPrompt;

        // Spoken punctuation commands become symbols.
        Assert.Contains("spoken punctuation into symbols", prompt);
        // Spoken layout commands create lines/paragraphs.
        Assert.Contains("new paragraph", prompt);
        // Grammar cleanup.
        Assert.Contains("Fix grammar", prompt);
        // Context-aware formatting: email, chat (no trailing period), code/terminal minimal edits.
        Assert.Contains("Context email adds greeting and sign-off", prompt);
        Assert.Contains("Context chat stays conversational and omits the period", prompt);
        Assert.Contains("add no punctuation that was not spoken", prompt);
    }

    [Fact]
    public void Build_MapsCategoryToContextControlValues()
    {
        Assert.Contains("[Context: chat]", DictationPromptAdapter.Build(
            new RawTranscript("hey"),
            new TranscriptStyle("PersonalMessaging", "casual", "conversational prose")));
        Assert.Contains("[Context: chat]", DictationPromptAdapter.Build(
            new RawTranscript("hey"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.WorkMessaging)));
        Assert.Contains("[Context: code]", DictationPromptAdapter.Build(
            new RawTranscript("x"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.CodeEditor)));
        Assert.Contains("[Context: terminal]", DictationPromptAdapter.Build(
            new RawTranscript("x"),
            TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal)));
        Assert.Contains("[Context: general]", DictationPromptAdapter.Build(
            new RawTranscript("one two three"),
            new TranscriptStyle("Unknown", "unsupported", "Unknown")));
    }
}
