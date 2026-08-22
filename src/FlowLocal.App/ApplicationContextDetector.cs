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
                domain = await _detectDomain(target, browser, cancellationToken).ConfigureAwait(false);
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
