using FlowLocal.Core;

namespace FlowLocal.App;

public static class TranscriptStyleResolver
{
    public static TranscriptStyle Resolve(OutputContextCategory category) => category switch
    {
        OutputContextCategory.Email => new("Email", "semi-formal", "email-aware", EnableLists: true, EnableEmailFormatting: true),
        OutputContextCategory.WorkMessaging => new("WorkMessaging", "semi-casual", "concise prose", EnableLists: true),
        OutputContextCategory.PersonalMessaging => new("PersonalMessaging", "casual", "conversational prose"),
        OutputContextCategory.Document => new("Document", "neutral", "prose", EnableLists: true),
        OutputContextCategory.AiChat => new("AiChat", "neutral", "preserve requested content", EnableLists: true),
        OutputContextCategory.CodeEditor => new("CodeEditor", "neutral", "minimally transformed prose", PreserveTechnicalTokens: true, UseSmartPunctuation: false),
        OutputContextCategory.Terminal => new("Terminal", "raw", "minimal", UseStandardCapitalization: false, EnableParagraphs: false, EnableLists: false, EnableEmailFormatting: false, PreserveTechnicalTokens: true, UseSmartPunctuation: false),
        OutputContextCategory.General => new("General", "neutral", "prose", EnableLists: true),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };
}
