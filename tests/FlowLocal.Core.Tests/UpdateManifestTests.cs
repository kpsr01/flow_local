using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class UpdateManifestTests
{
    [Fact]
    public void Parse_ValidManifest_ReturnsFields()
    {
        var manifest = UpdateManifest.TryParse(
            """{"version":"1.2.3","url":"https://example.com/FlowLocal-1.2.3-win-x64-setup.exe","sha256":"AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899"}""");
        Assert.NotNull(manifest);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("https://example.com/FlowLocal-1.2.3-win-x64-setup.exe", manifest.Url);
        Assert.Equal("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", manifest.Sha256);
    }

    [Theory]
    [InlineData("""{"Version":"2.0.0","Url":"https://example.com/setup.exe"}""")]
    [InlineData("""{"Version":"2.0.0","Url":"https://example.com/setup.exe","Sha256":"ABC"}""")]
    public void Parse_MissingOrInvalidHash_ReturnsNull(string json)
    {
        Assert.Null(UpdateManifest.TryParse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"version":"1.0.0"}""")]
    [InlineData("""{"version":"1.0.0","url":"http://example.com/setup.exe"}""")]
    [InlineData("""{"version":"1.0.0","url":"not a url"}""")]
    [InlineData("""{"version":"","url":"https://example.com/setup.exe"}""")]
    [InlineData("""{"version":"next","url":"https://example.com/setup.exe","sha256":"aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899"}""")]
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
        var manifest = new UpdateManifest(candidate, "https://example.com/setup.exe", new string('a', 64));
        Assert.Equal(expected, manifest.IsNewerThan(Version.Parse(current)));
    }

    [Fact]
    public void IsNewerThan_UnparsableCandidate_IsNeverNewer()
    {
        var manifest = new UpdateManifest("next", "https://example.com/setup.exe", new string('a', 64));
        Assert.False(manifest.IsNewerThan(new Version(1, 0, 0)));
    }
}
