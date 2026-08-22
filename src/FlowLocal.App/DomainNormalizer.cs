namespace FlowLocal.App;

public static class DomainNormalizer
{
    private static readonly HashSet<string> AllowedSchemes =
    [
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeFtp,
        "ws",
        "wss"
    ];

    public static string? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !AllowedSchemes.Contains(uri.Scheme) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return null;

        var host = uri.IdnHost.TrimEnd('.');
        return host.Length == 0 ? null : host.ToLowerInvariant();
    }
}
