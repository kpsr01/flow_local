using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class OutputStyleClassifierTests
{
    private readonly OutputStyleClassifier classifier = new();

    public static TheoryData<string, string?, bool, OutputContextCategory, ClassificationSource> Categories => new()
    {
        { "outlook.exe", null, false, OutputContextCategory.Email, ClassificationSource.KnownApplication },
        { "slack.exe", null, false, OutputContextCategory.WorkMessaging, ClassificationSource.KnownApplication },
        { "whatsapp.exe", null, false, OutputContextCategory.PersonalMessaging, ClassificationSource.KnownApplication },
        { "notion.exe", null, false, OutputContextCategory.Document, ClassificationSource.KnownApplication },
        { "chrome.exe", "chatgpt.com", true, OutputContextCategory.AiChat, ClassificationSource.KnownDomain },
        { "code.exe", null, false, OutputContextCategory.CodeEditor, ClassificationSource.KnownApplication },
        { "pwsh.exe", null, false, OutputContextCategory.Terminal, ClassificationSource.KnownApplication },
        { "unknown.exe", null, false, OutputContextCategory.General, ClassificationSource.General }
    };

    [Theory]
    [MemberData(nameof(Categories))]
    public void Classify_CoversEveryCategory(string executable, string? domain, bool browser, OutputContextCategory category, ClassificationSource source)
    {
        var result = classifier.Classify(Context(executable, domain, browser), new());

        Assert.Equal(category, result.Category);
        Assert.Equal(source, result.Source);
        Assert.Equal(category.ToString(), result.Style.Category);
        Assert.Same(ContextDiagnostic, result.Diagnostic);
    }

    [Theory]
    [InlineData("mail.google.com", OutputContextCategory.Email)]
    [InlineData("docs.google.com", OutputContextCategory.Document)]
    [InlineData("chatgpt.com", OutputContextCategory.AiChat)]
    [InlineData("slack.com", OutputContextCategory.WorkMessaging)]
    public void Classify_ProjectBrowserExamplesPreferDomainOverChrome(string domain, OutputContextCategory expected)
    {
        var result = classifier.Classify(Context("chrome.exe", domain, true), new());

        Assert.Equal(expected, result.Category);
        Assert.Equal(ClassificationSource.KnownDomain, result.Source);
        Assert.Equal(domain, result.Rule);
    }

    [Fact]
    public void Classify_DomainOverrideBeatsExecutableOverrideAndKnownRules()
    {
        var domainStyle = TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal);
        var executableStyle = TranscriptStyleResolver.Resolve(OutputContextCategory.Document);
        var settings = new OutputStyleSettings(
            DomainOverrides: new Dictionary<string, OutputStyleOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["HTTPS://SLACK.COM/"] = new(OutputContextCategory.Terminal, domainStyle)
            },
            ExecutableOverrides: new Dictionary<string, OutputStyleOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["CHROME.EXE"] = new(OutputContextCategory.Document, executableStyle)
            });

        var result = classifier.Classify(Context("C:\\Apps\\Chrome.EXE", "team.SLACK.com", true), settings);

        Assert.Equal(OutputContextCategory.Terminal, result.Category);
        Assert.Equal(domainStyle, result.Style);
        Assert.Equal(ClassificationSource.DomainOverride, result.Source);
        Assert.Equal("slack.com", result.Rule);
    }

    [Fact]
    public void Classify_ExecutableOverrideBeatsKnownDomainApplicationAndControlHint()
    {
        var style = TranscriptStyleResolver.Resolve(OutputContextCategory.CodeEditor);
        var settings = new OutputStyleSettings(
            ExecutableOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["C:\\Apps\\OUTLOOK.EXE"] = new(OutputContextCategory.CodeEditor, style)
            });

        var result = classifier.Classify(Context("outlook.exe", "docs.google.com", true, "Document"), settings);

        Assert.Equal(OutputContextCategory.CodeEditor, result.Category);
        Assert.Equal(style, result.Style);
        Assert.Equal(ClassificationSource.ExecutableOverride, result.Source);
    }

    [Fact]
    public void Classify_DiscordAmbiguityCanBeOverriddenAsWork()
    {
        var style = TranscriptStyleResolver.Resolve(OutputContextCategory.WorkMessaging);
        var settings = new OutputStyleSettings(
            ExecutableOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["discord.exe"] = new(OutputContextCategory.WorkMessaging, style)
            });

        var result = classifier.Classify(Context("DISCORD.EXE"), settings);

        Assert.Equal(OutputContextCategory.WorkMessaging, result.Category);
        Assert.Equal(ClassificationSource.ExecutableOverride, result.Source);
    }

    [Theory]
    [InlineData("chatgpt.com", OutputContextCategory.AiChat, ClassificationSource.KnownDomain)]
    [InlineData("labs.chatgpt.com", OutputContextCategory.AiChat, ClassificationSource.KnownDomain)]
    [InlineData("notchatgpt.com", OutputContextCategory.General, ClassificationSource.GenericBrowser)]
    [InlineData("chatgpt.com.evil.test", OutputContextCategory.General, ClassificationSource.GenericBrowser)]
    public void Classify_DomainRulesMatchOnlyExactHostsOrRealSubdomains(string domain, OutputContextCategory category, ClassificationSource source)
    {
        var result = classifier.Classify(Context("chrome.exe", domain, true), new());

        Assert.Equal(category, result.Category);
        Assert.Equal(source, result.Source);
    }

    [Fact]
    public void Classify_WebsiteDetectionDisabledSkipsDomainOverrideAndKnownDomain()
    {
        var settings = new OutputStyleSettings(
            WebsiteDetectionEnabled: false,
            DomainOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["chatgpt.com"] = new(OutputContextCategory.Terminal, TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal))
            });

        var result = classifier.Classify(Context("chrome.exe", "chatgpt.com", true), settings);

        Assert.Equal(OutputContextCategory.General, result.Category);
        Assert.Equal(ClassificationSource.GenericBrowser, result.Source);
    }

    [Fact]
    public void Classify_DisabledClassificationUsesUniversalCategoryAndStyle()
    {
        var universal = new TranscriptStyle("Universal", "casual", "prose", EnableLists: true);
        var settings = new OutputStyleSettings(
            StyleClassificationEnabled: false,
            UniversalDefaultCategory: OutputContextCategory.PersonalMessaging,
            UniversalDefaultStyle: universal);

        var result = classifier.Classify(Context("outlook.exe", "mail.google.com", true), settings);

        Assert.Equal(OutputContextCategory.PersonalMessaging, result.Category);
        Assert.Equal(universal, result.Style);
        Assert.Equal(ClassificationSource.General, result.Source);
        Assert.Equal("StyleClassificationDisabled", result.Rule);
    }

    [Fact]
    public void Classify_DisabledClassificationResolvesUniversalCategoryWhenStyleIsAbsent()
    {
        var result = classifier.Classify(
            Context("outlook.exe"),
            new OutputStyleSettings(StyleClassificationEnabled: false, UniversalDefaultCategory: OutputContextCategory.Document));

        Assert.Equal(TranscriptStyleResolver.Resolve(OutputContextCategory.Document), result.Style);
    }

    [Fact]
    public void Classify_UsesEveryRemainingPriorityInOrder()
    {
        var control = classifier.Classify(Context("unknown.exe", controlType: "Document"), new());
        var browser = classifier.Classify(Context("chrome.exe", browser: true), new());
        var general = classifier.Classify(Context("unknown.exe"), new());

        Assert.Equal(ClassificationSource.ControlHint, control.Source);
        Assert.Equal(OutputContextCategory.Document, control.Category);
        Assert.Equal(ClassificationSource.GenericBrowser, browser.Source);
        Assert.Equal(ClassificationSource.General, general.Source);
    }

    private static readonly ContextDetectionDiagnostic ContextDiagnostic =
        new(ContextDetectionConfidence.High, "test-source");

    private static ApplicationContext Context(
        string executable,
        string? domain = null,
        bool browser = false,
        string? controlType = null) =>
        new(executable, executable, "Window", controlType, browser,
            browser ? BrowserIdentity.Chrome : null, domain, ContextDiagnostic);
}
