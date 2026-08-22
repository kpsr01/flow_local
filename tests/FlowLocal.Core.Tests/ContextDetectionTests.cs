using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class ContextDetectionTests
{
    [Fact]
    public async Task DetectAsync_UnknownApplicationReturnsGeneralContextWithoutBrowserProbe()
    {
        var probed = false;
        var detector = new ApplicationContextDetector((_, _, _) =>
        {
            probed = true;
            return Task.FromResult<string?>(null);
        });

        var context = await detector.DetectAsync(Target("AcmeWriter.exe", "Private draft", "Document"), true, default);

        Assert.False(probed);
        Assert.Equal("acmewriter", context.ExecutableName);
        Assert.Equal("AcmeWriter", context.DisplayName);
        Assert.Equal("Private draft", context.WindowTitle);
        Assert.Equal("Document", context.ControlType);
        Assert.False(context.IsBrowser);
        Assert.Null(context.Browser);
        Assert.Null(context.Domain);
        Assert.Equal(ContextDetectionConfidence.High, context.Detection.Confidence);
    }

    [Fact]
    public async Task DetectAsync_BrowserWithoutDomainReturnsGenericBrowserContext()
    {
        var detector = new ApplicationContextDetector((_, browser, _) =>
        {
            Assert.Equal(BrowserIdentity.Chrome, browser);
            return Task.FromResult<string?>(null);
        });

        var context = await detector.DetectAsync(Target("chrome.exe", "Sensitive page title"), true, default);

        Assert.True(context.IsBrowser);
        Assert.Equal(BrowserIdentity.Chrome, context.Browser);
        Assert.Equal("Google Chrome", context.DisplayName);
        Assert.Null(context.Domain);
        Assert.Equal("ForegroundWindow", context.Detection.Source);
    }

    [Fact]
    public async Task DetectAsync_BrowserProbeFailureIsNonfatalAndDoesNotInventDomain()
    {
        var detector = new ApplicationContextDetector((_, _, _) =>
            Task.FromException<string?>(new InvalidOperationException("provider unavailable")));

        var context = await detector.DetectAsync(Target("msedge.exe", "Bank account 1234"), true, default);

        Assert.True(context.IsBrowser);
        Assert.Equal(BrowserIdentity.Edge, context.Browser);
        Assert.Null(context.Domain);
        Assert.Equal(ContextDetectionConfidence.Low, context.Detection.Confidence);
        Assert.Equal("ForegroundWindow", context.Detection.Source);
        Assert.Contains("provider unavailable", context.Detection.Error);
    }

    [Fact]
    public async Task DetectAsync_WebsiteDetectionDisabledDoesNotProbeBrowser()
    {
        var probed = false;
        var detector = new ApplicationContextDetector((_, _, _) =>
        {
            probed = true;
            return Task.FromResult<string?>("private.example");
        });

        var context = await detector.DetectAsync(Target("chrome.exe", "Sensitive page title"), false, default);

        Assert.False(probed);
        Assert.True(context.IsBrowser);
        Assert.Equal("Google Chrome", context.DisplayName);
        Assert.Null(context.Domain);
        Assert.Equal("ForegroundWindow", context.Detection.Source);
    }

    private static ActiveTarget Target(string executableName, string windowTitle, string? controlType = null) =>
        new(42, (nint)123, executableName, windowTitle, DateTimeOffset.UnixEpoch,
            FocusedControlType: controlType);
}
