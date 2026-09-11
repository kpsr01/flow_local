using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class ApplicationContextDetector : IApplicationContextDetector
{
    private readonly Func<ActiveTarget, BrowserIdentity, CancellationToken, Task<string?>> _detectDomain;

    public ApplicationContextDetector(BrowserContextDetector browserContextDetector)
        : this(browserContextDetector.DetectDomainAsync)
    {
    }

    internal ApplicationContextDetector(
        Func<ActiveTarget, BrowserIdentity, CancellationToken, Task<string?>> detectDomain) =>
        _detectDomain = detectDomain;

    public async Task<ApplicationContext> DetectAsync(ActiveTarget target, bool detectWebsite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var application = ApplicationNameCatalog.Normalize(target.ExecutableName);
        var diagnostic = new ContextDetectionDiagnostic(ContextDetectionConfidence.High, "ForegroundWindow");
        string? domain = null;

        if (detectWebsite && application.Browser is { } browser)
        {
            try
            {
                domain = await _detectDomain(target, browser, cancellationToken)
                    .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                if (domain is not null)
                    diagnostic = new ContextDetectionDiagnostic(ContextDetectionConfidence.High, "BrowserAddressBar");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostic = new ContextDetectionDiagnostic(
                    ContextDetectionConfidence.Low,
                    "ForegroundWindow",
                    $"Browser domain detection failed: {exception.Message}");
            }
        }

        if (detectWebsite && application.Browser is not null && domain is null)
        {
            domain = DomainNormalizer.TryNormalize(target.WindowTitle);
            if (domain is null)
            {
                // ponytail: branded title segments only; no extension or page-content scraping.
                var segments = target.WindowTitle.Split([" - ", " — ", " | ", " – "], StringSplitOptions.TrimEntries);
                domain = segments.Select(segment => segment.ToLowerInvariant() switch
                {
                    "gmail" => "mail.google.com",
                    "slack" => "slack.com",
                    "microsoft teams" => "teams.microsoft.com",
                    "google docs" => "docs.google.com",
                    _ => null
                }).FirstOrDefault(value => value is not null);
            }
            if (domain is not null)
                diagnostic = new ContextDetectionDiagnostic(ContextDetectionConfidence.Low, "BrowserWindowTitle");
        }

        return new ApplicationContext(
            application.ExecutableName,
            application.DisplayName,
            target.WindowTitle ?? "",
            target.FocusedControlType,
            application.Browser is not null,
            application.Browser,
            domain,
            diagnostic);
    }
}
