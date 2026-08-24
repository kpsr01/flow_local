using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class DictationPromptAdapterTests
{
    // Exact completion format from the model card
    // (https://huggingface.co/juanquivilla/sotto-cleanup-lfm25-350m). The model
    // was trained on this fixed layout; any drift can degrade its output.
    [Fact]
    public void Build_UsesSottoInputOutputFormatWithoutChatTemplate()
    {
        var prompt = DictationPromptAdapter.Build(new RawTranscript("um so i i think we should ship this on uh friday"));

        Assert.Equal(
            "### Input:\num so i i think we should ship this on uh friday\n\n### Output:\n",
            prompt);
        // The fine-tune was trained without a chat template, system prompt,
        // style controls, or a think block.
        Assert.DoesNotContain("<|im_start|>", prompt);
        Assert.DoesNotContain("<|startoftext|>", prompt);
        Assert.DoesNotContain("[Styling:", prompt);
        Assert.DoesNotContain("<think>", prompt);
    }
}
