using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class CodingRequestNormalizerTests
{
    [Theory]
    [InlineData("um could you please fix src/auth.ts without changing the API", "fix src/auth.ts without changing the API")]
    [InlineData("I want you to investigate why getUser returns null", "investigate why getUser returns null")]
    [InlineData("please explain the failure", "explain the failure")]
    [InlineData("could you fix this?", "could you fix this?")]
    [InlineData("can you access the server", "can you access the server")]
    [InlineData("I want to understand the issue", "I want to understand the issue")]
    [InlineData("do not fix this yet", "do not fix this yet")]
    [InlineData("print please fix this", "print please fix this")]
    [InlineData("maybe fix src/auth.ts", "maybe fix src/auth.ts")]
    public void NormalizesOnlyExplicitRequestPrefixes(string source, string expected)
    {
        var raw = new RawTranscript(source);
        Assert.Equal(expected, CodingRequestNormalizer.Normalize(raw).Text);
        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw, new(expected)));
        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw, new(source)));
    }

    [Fact]
    public void DirectRequestStillCannotLoseConstraintsOrGainRequirements()
    {
        var raw = new RawTranscript("could you please fix getUser do not change the API");
        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new("Task:\nfix getUser\n\nConstraints:\ndo not change the API.")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(raw, new("fix getUser")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new("fix getUser do not change the API and add tests")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new("fix getuser do not change the API")));
    }

    [Fact]
    public void DecodingAndPromptUseTheSameNormalizedRequest()
    {
        var raw = new RawTranscript("could you please fix src/auth.ts");
        var policy = PromptPolicyRegistry.Generic(new("test", null, null, "test"));
        Assert.Contains("<|im_start|>user\nfix src/auth.ts<|im_end|>", DictationPromptAdapter.Build(raw, policy));
        var grammar = CodingCleanupValidator.CreateFormattingGrammar(raw);
        Assert.DoesNotContain("\"could\"", grammar);
        Assert.Contains("\"src/auth.ts\"", grammar);
    }

    [Theory]
    [InlineData("openai-codex/gpt-5.3-codex", "OpenAI Codex")]
    [InlineData("anthropic/claude-opus-5[1m]", "Claude Opus 5")]
    [InlineData("openai/gpt-5", "GPT-5")]
    [InlineData("gateway/gpt-5", "unknown")]
    [InlineData("openai/unknown-model", "unknown")]
    public void ProviderQualifiedModelsSelectOnlyKnownGuides(string model, string family)
    {
        var policy = PromptPolicyRegistry.Default.Get(new("Pi", model, "high", "test"));
        Assert.Equal(family, policy.ModelFamily);
        Assert.Equal(model, policy.TargetModel);
    }

    [Fact]
    public void MixedImperativeRequestsAnchorConstraintsAndDeliverables()
    {
        var grammar = CodingCleanupValidator.CreateFormattingGrammar(new(
            "fix the login bug keep the API unchanged do not add dependencies return a summary"));
        Assert.Contains("\"bug\" punct? \"\\n\\n\" constraints", grammar);
        Assert.Contains("\"unchanged\" punct? \"\\n\"", grammar);
        Assert.Contains("\"dependencies\" punct? \"\\n\\n\" output", grammar);
    }

    [Theory]
    [InlineData("fix the login bug only if we keep the old API")]
    [InlineData("review why we do not return a response")]
    [InlineData("fix the message containing `do not return a response`")]
    [InlineData("why does the handler return a response")]
    public void ConditionalQuestionsAndCodeDoNotGetForcedClauseHeadings(string text)
    {
        var grammar = CodingCleanupValidator.CreateFormattingGrammar(new(text));
        Assert.DoesNotContain("punct? \"\\n\\n\"", grammar);
    }

    [Theory]
    [InlineData("fix the handler do not return a response")]
    [InlineData("fix the handler do not explain the implementation")]
    [InlineData("update the handler to return a response immediately")]
    [InlineData("update the handler it should return a response")]
    public void DoesNotSplitVerbsFromNegationsOrModalScope(string text)
    {
        var root = CodingCleanupValidator.CreateFormattingGrammar(new(text)).Split('\n')[0];
        Assert.DoesNotContain(" output", root);
    }
}
