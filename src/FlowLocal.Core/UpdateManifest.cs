using System.Text.Json;

namespace FlowLocal.Core;

/// <summary>Hosted update manifest (latest.json): the newest released version and its installer.</summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses and validates the release manifest at the network trust boundary.</summary>
    public static UpdateManifest? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            return manifest is null
                || string.IsNullOrWhiteSpace(manifest.Version)
                || !System.Version.TryParse(manifest.Version.Trim(), out _)
                || string.IsNullOrWhiteSpace(manifest.Url)
                || !Uri.IsWellFormedUriString(manifest.Url, UriKind.Absolute)
                || !manifest.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(manifest.Sha256)
                || manifest.Sha256.Trim().Length != 64
                || manifest.Sha256.Trim().Any(character => !char.IsAsciiHexDigit(character))
                ? null
                : manifest with
                {
                    Version = manifest.Version.Trim(),
                    Url = manifest.Url.Trim(),
                    Sha256 = manifest.Sha256.Trim().ToLowerInvariant()
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the manifest version parses and is strictly newer than the running version.</summary>
    public bool IsNewerThan(Version current) =>
        System.Version.TryParse(Version, out var candidate) && candidate > current;
}
