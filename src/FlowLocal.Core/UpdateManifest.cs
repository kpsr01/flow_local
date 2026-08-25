using System.Text.Json;

namespace FlowLocal.Core;

/// <summary>Hosted update manifest (latest.json): the newest released version and its installer.</summary>
public sealed record UpdateManifest(string Version, string Url, string? Sha256)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses the manifest JSON; returns null when malformed or missing required fields.</summary>
    public static UpdateManifest? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            return manifest is null
                || string.IsNullOrWhiteSpace(manifest.Version)
                || string.IsNullOrWhiteSpace(manifest.Url)
                || !Uri.IsWellFormedUriString(manifest.Url, UriKind.Absolute)
                || !manifest.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? null
                : manifest with { Version = manifest.Version.Trim(), Url = manifest.Url };
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
