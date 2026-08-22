using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class ClassificationDiagnosticsTests
{
    [Fact]
    public void ClassificationDiagnostics_ExposeNormalizedDomainButNeverRawUrl()
    {
        const string rawUrl = "https://CHATGPT.com/private/conversation?token=super-secret#answer";
        var domain = DomainNormalizer.TryNormalize(rawUrl);
        var context = new ApplicationContext(
            "chrome",
            "Google Chrome",
            "Private conversation title",
            "Document",
            true,
            BrowserIdentity.Chrome,
            domain,
            new ContextDetectionDiagnostic(ContextDetectionConfidence.High, "BrowserAddressBar"));

        var classification = new OutputStyleClassifier().Classify(context, new OutputStyleSettings());
        var diagnostic = string.Join('|',
            context.ExecutableName,
            context.DisplayName,
            context.Domain,
            classification.Category,
            classification.Style.Category,
            classification.Source,
            classification.Rule,
            classification.Diagnostic.Error);

        Assert.Equal("chatgpt.com", context.Domain);
        Assert.Contains("chatgpt.com", diagnostic);
        Assert.DoesNotContain(rawUrl, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/conversation", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private conversation title", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionError_IsRetainedWithoutLeakingRawUrlThroughClassificationFields()
    {
        var diagnostic = new ContextDetectionDiagnostic(
            ContextDetectionConfidence.Low,
            "ForegroundWindow",
            "Browser domain detection failed");
        var context = new ApplicationContext(
            "chrome",
            "Google Chrome",
            "ignored title",
            null,
            true,
            BrowserIdentity.Chrome,
            null,
            diagnostic);

        var result = new OutputStyleClassifier().Classify(context, new OutputStyleSettings());
        var displayed = string.Join('|', context.Domain, result.Category, result.Style.Category, result.Source, result.Rule, result.Diagnostic.Error);

        Assert.Same(diagnostic, result.Diagnostic);
        Assert.Contains("Browser domain detection failed", displayed);
        Assert.DoesNotContain("http://", displayed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", displayed, StringComparison.OrdinalIgnoreCase);
    }
}
