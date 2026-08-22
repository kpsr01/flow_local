using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class ApplicationNameCatalogTests
{
    [Theory]
    [InlineData("CHROME.EXE", "chrome", "Google Chrome", BrowserIdentity.Chrome)]
    [InlineData(@"C:\Program Files\Microsoft\Edge\msedge.exe", "msedge", "Microsoft Edge", BrowserIdentity.Edge)]
    [InlineData("firefox", "firefox", "Mozilla Firefox", BrowserIdentity.Firefox)]
    [InlineData("WINWORD.EXE", "winword", "Microsoft Word", null)]
    [InlineData("WindowsTerminal.exe", "windowsterminal", "Windows Terminal", null)]
    public void Normalize_KnownExecutables(string input, string executable, string displayName, BrowserIdentity? browser)
    {
        var result = ApplicationNameCatalog.Normalize(input);

        Assert.Equal(executable, result.ExecutableName);
        Assert.Equal(displayName, result.DisplayName);
        Assert.Equal(browser, result.Browser);
    }

    [Fact]
    public void Normalize_UnknownExecutableUsesBasenameAsDisplayFallback()
    {
        var result = ApplicationNameCatalog.Normalize(@"C:\Tools\AcmeWriter.EXE");

        Assert.Equal("acmewriter", result.ExecutableName);
        Assert.Equal("AcmeWriter", result.DisplayName);
        Assert.Null(result.Browser);
    }
}
