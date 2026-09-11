using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class DictationPromptAdapterTests
{
    [Fact]
    public void Build_UsesCompactLfmChatFormatAndPreservesTechnicalInstruction()
    {
        var prompt = DictationPromptAdapter.Build(new RawTranscript("um fix getUserById in src/auth/session.ts"));

        Assert.Contains("<|im_start|>system", prompt);
        Assert.Contains("Copy technical identifiers", prompt);
        Assert.Contains("getUserById", prompt);
        Assert.Contains("src/auth/session.ts", prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
        Assert.DoesNotContain("<think>", prompt);
    }
}
