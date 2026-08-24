using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>
/// Builds prompts in the exact completion format Sotto was trained on
/// (https://huggingface.co/juanquivilla/sotto-cleanup-lfm25-350m): a plain
/// "### Input:" / "### Output:" block with no chat template and no system
/// prompt. Do not add instructions or control lines — extra prompting degrades
/// a base-model fine-tune trained on this single fixed format.
/// </summary>
internal static class DictationPromptAdapter
{
    internal static string Build(RawTranscript transcript) =>
        $"### Input:\n{transcript.Text}\n\n### Output:\n";
}
