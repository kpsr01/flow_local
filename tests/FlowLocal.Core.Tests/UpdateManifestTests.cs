using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class UpdateManifestTests
{
    [Fact]
    public void Parse_ValidManifest_ReturnsFields()
    {
        var manifest = UpdateManifest.TryParse(
            """{"version":"1.2.3","url":"https://example.com/FlowLocal-1.2.3-win-x64-setup.exe","sha256":"ABC"}""");
        Assert.NotNull(manifest);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("https://example.com/FlowLocal-1.2.3-win-x64-setup.exe", manifest.Url);
        Assert.Equal("ABC", manifest.Sha256);
    }

    [Fact]
    public void Parse_IsCaseInsensitiveAndToleratesMissingHash()
    {
        var manifest = UpdateManifest.TryParse("""{"Version":"2.0.0","Url":"https://example.com/setup.exe"}""");
        Assert.NotNull(manifest);
        Assert.Null(manifest.Sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"version":"1.0.0"}""")]
    [InlineData("""{"version":"1.0.0","url":"http://example.com/setup.exe"}""")]
    [InlineData("""{"version":"1.0.0","url":"not a url"}""")]
    [InlineData("""{"version":"","url":"https://example.com/setup.exe"}""")]
    public void Parse_InvalidManifest_ReturnsNull(string json)
    {
        Assert.Null(UpdateManifest.TryParse(json));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("2.0", "1.9.9", true)]
    public void IsNewerThan_ComparesNumerically(string candidate, string current, bool expected)
    {
        var manifest = new UpdateManifest(candidate, "https://example.com/setup.exe", null);
        Assert.Equal(expected, manifest.IsNewerThan(Version.Parse(current)));
    }

    [Fact]
    public void IsNewerThan_UnparsableCandidate_IsNeverNewer()
    {
        var manifest = new UpdateManifest("next", "https://example.com/setup.exe", null);
        Assert.False(manifest.IsNewerThan(new Version(1, 0, 0)));
    }
}
