using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class DomainNormalizerTests
{
    [Theory]
    [InlineData("http://example.com", "example.com")]
    [InlineData("https://EXAMPLE.COM./private?token=secret#fragment", "example.com")]
    [InlineData("ftp://files.example.com/archive", "files.example.com")]
    [InlineData("ws://socket.example.com/chat", "socket.example.com")]
    [InlineData("wss://socket.example.com/chat", "socket.example.com")]
    [InlineData("https://bücher.example/path", "xn--bcher-kva.example")]
    public void TryNormalize_ReturnsOnlyNormalizedHostname(string input, string expected)
    {
        Assert.Equal(expected, DomainNormalizer.TryNormalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com/path")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("mailto:user@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.com/private")]
    [InlineData("not a uri")]
    public void TryNormalize_RejectsUnsafeOrUnsupportedForms(string? input)
    {
        Assert.Null(DomainNormalizer.TryNormalize(input));
    }
}
