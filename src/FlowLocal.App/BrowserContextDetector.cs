using System.Runtime.InteropServices;
using System.Windows.Automation;
using FlowLocal.Core;
using System.IO;

namespace FlowLocal.App;

public sealed class BrowserContextDetector : IApplicationContextDetector
{
    private static readonly TimeSpan DetectionBudget = TimeSpan.FromMilliseconds(200);
    private static readonly string[] ChromeHints = ["toolbar", "navigation", "browser", "omnibox", "address", "urlbar"];

    public Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken)
    {
        var browser = GetBrowser(target.ExecutableName);
        if (browser is null)
            return Task.FromResult(General(target));

        return DetectContextAsync(target, browser.Value, cancellationToken);
    }

    public Task<string?> DetectDomainAsync(
        ActiveTarget target,
        BrowserIdentity browser,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => DetectDomain(target, browser, cancellationToken),
            CancellationToken.None);

    private static Task<ApplicationContext> DetectContextAsync(
        ActiveTarget target,
        BrowserIdentity browser,
        CancellationToken cancellationToken) =>
        Task.Run(() => DetectBrowser(target, browser, cancellationToken), CancellationToken.None);

    private static ApplicationContext DetectBrowser(
        ActiveTarget target,
        BrowserIdentity browser,
        CancellationToken cancellationToken)
    {
        var domain = DetectDomain(target, browser, cancellationToken);
        var source = domain is null ? "none" : "uia-value";
        if (domain is null)
        {
            domain = DomainNormalizer.TryNormalize(target.WindowTitle);
            if (domain is not null)
                source = "strict-title";
        }

        return new ApplicationContext(
            target.ExecutableName,
            BrowserName(browser),
            target.WindowTitle,
            target.FocusedControlType,
            true,
            browser,
            domain,
            new ContextDetectionDiagnostic(
                domain is null ? ContextDetectionConfidence.Low : ContextDetectionConfidence.High,
                source));
    }

    private static string? DetectDomain(
        ActiveTarget target,
        BrowserIdentity browser,
        CancellationToken cancellationToken)
    {
        try
        {
            var deadline = DateTime.UtcNow + DetectionBudget;
            if (cancellationToken.IsCancellationRequested || target.WindowHandle == 0)
                return null;

            return FindDomain(AutomationElement.FromHandle(target.WindowHandle), browser, deadline, cancellationToken);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return null;
        }
    }

    private static string? FindDomain(
        AutomationElement root,
        BrowserIdentity browser,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var chrome = walker.GetFirstChild(root); chrome is not null; chrome = walker.GetNextSibling(chrome))
        {
            if (Expired(deadline, cancellationToken))
                return null;

            var current = chrome.Current;
            if (current.ControlType != ControlType.ToolBar && !HasChromeHint(current.AutomationId, current.Name))
                continue;

            var domain = FindDomainInChrome(chrome, browser, deadline, cancellationToken);
            if (domain is not null)
                return domain;
        }
        return null;
    }

    private static string? FindDomainInChrome(
        AutomationElement chrome,
        BrowserIdentity browser,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(chrome);
        while (pending.Count > 0)
        {
            if (Expired(deadline, cancellationToken))
                return null;

            var element = pending.Pop();
            var current = element.Current;
            if (current.ControlType == ControlType.Edit &&
                current.IsEnabled && !current.IsOffscreen && !current.IsPassword &&
                (browser != BrowserIdentity.Firefox ||
                 string.Equals(current.AutomationId, "urlbar-input", StringComparison.OrdinalIgnoreCase) ||
                 HasChromeHint(current.AutomationId, current.Name)) &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var domain = DomainNormalizer.TryNormalize(((ValuePattern)pattern).Current.Value);
                if (domain is not null)
                    return domain;
            }

            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child))
                pending.Push(child);
        }
        return null;
    }

    private static bool HasChromeHint(string? automationId, string? name) =>
        ChromeHints.Any(hint =>
            automationId?.Contains(hint, StringComparison.OrdinalIgnoreCase) == true ||
            name?.Contains(hint, StringComparison.OrdinalIgnoreCase) == true);

    private static bool Expired(DateTime deadline, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || DateTime.UtcNow >= deadline;

    private static BrowserIdentity? GetBrowser(string executableName) =>
        Path.GetFileNameWithoutExtension(executableName).ToLowerInvariant() switch
        {
            "chrome" => BrowserIdentity.Chrome,
            "msedge" => BrowserIdentity.Edge,
            "firefox" => BrowserIdentity.Firefox,
            _ => null
        };

    private static string BrowserName(BrowserIdentity browser) => browser switch
    {
        BrowserIdentity.Chrome => "Google Chrome",
        BrowserIdentity.Edge => "Microsoft Edge",
        BrowserIdentity.Firefox => "Mozilla Firefox",
        _ => "Browser"
    };

    private static ApplicationContext General(ActiveTarget target) => new(
        target.ExecutableName,
        target.ExecutableName,
        target.WindowTitle,
        target.FocusedControlType,
        false,
        null,
        null,
        new ContextDetectionDiagnostic(ContextDetectionConfidence.None, "none"));

    private static bool IsProviderFailure(Exception exception) => exception is
        ElementNotAvailableException or
        InvalidOperationException or
        NotSupportedException or
        COMException or
        UnauthorizedAccessException;
}
