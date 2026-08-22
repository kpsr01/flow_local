using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class TranscriptStyleResolverTests
{
    public static TheoryData<OutputContextCategory, TranscriptStyle> Profiles => new()
    {
        { OutputContextCategory.Email, new("Email", "semi-formal", "email-aware", EnableLists: true, EnableEmailFormatting: true) },
        { OutputContextCategory.WorkMessaging, new("WorkMessaging", "semi-casual", "concise prose", EnableLists: true) },
        { OutputContextCategory.PersonalMessaging, new("PersonalMessaging", "casual", "conversational prose") },
        { OutputContextCategory.Document, new("Document", "neutral", "prose", EnableLists: true) },
        { OutputContextCategory.AiChat, new("AiChat", "neutral", "preserve requested content", EnableLists: true) },
        { OutputContextCategory.CodeEditor, new("CodeEditor", "neutral", "minimally transformed prose", PreserveTechnicalTokens: true, UseSmartPunctuation: false) },
        { OutputContextCategory.Terminal, new("Terminal", "raw", "minimal", UseStandardCapitalization: false, EnableParagraphs: false, EnableLists: false, EnableEmailFormatting: false, PreserveTechnicalTokens: true, UseSmartPunctuation: false) },
        { OutputContextCategory.General, new("General", "neutral", "prose", EnableLists: true) }
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Resolve_MapsEveryCategoryToItsCompleteProfile(OutputContextCategory category, TranscriptStyle expected) =>
        Assert.Equal(expected, TranscriptStyleResolver.Resolve(category));

    [Fact]
    public void Resolve_EmailEnablesOnlyEmailAppropriateFeatures()
    {
        var style = TranscriptStyleResolver.Resolve(OutputContextCategory.Email);

        Assert.True(style.UseStandardCapitalization);
        Assert.True(style.EnableParagraphs);
        Assert.True(style.EnableLists);
        Assert.True(style.EnableEmailFormatting);
        Assert.True(style.UseSmartPunctuation);
    }

    [Fact]
    public void Resolve_AiChatNeverInventsEmailFormatting()
    {
        var style = TranscriptStyleResolver.Resolve(OutputContextCategory.AiChat);

        Assert.True(style.EnableParagraphs);
        Assert.True(style.EnableLists);
        Assert.False(style.EnableEmailFormatting);
    }

    [Fact]
    public void Resolve_TerminalPreservesTechnicalTextAndDisablesAutomaticFormatting()
    {
        var style = TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal);

        Assert.Equal("raw", style.Tone);
        Assert.Equal("minimal", style.Structure);
        Assert.False(style.UseStandardCapitalization);
        Assert.False(style.EnableParagraphs);
        Assert.False(style.EnableLists);
        Assert.False(style.EnableEmailFormatting);
        Assert.True(style.PreserveTechnicalTokens);
        Assert.False(style.UseSmartPunctuation);
    }
}
