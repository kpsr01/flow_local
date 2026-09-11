using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLocal.App;

public sealed record AppSettings(
    bool HandsFreeEnabled = false,
    int DoubleTapIntervalMilliseconds = 400,
    IReadOnlyList<string>? ShortcutModifiers = null,
    bool FollowDefaultMicrophone = true,
    string? PreferredMicrophoneDeviceId = null,
    double? OverlayLeft = null,
    double? OverlayTop = null,
    IReadOnlyList<string>? Vocabulary = null,
    IReadOnlyDictionary<string, string>? RememberedCorrections = null,
    string? EncryptedAssemblyAIApiKey = null,
    string? AsrProvider = null,
    string? RewriteProvider = null)
{
    public static readonly IReadOnlyList<string> DefaultShortcutModifiers = ["Ctrl", "Win"];

    [JsonIgnore]
    public string EffectiveAsrProvider =>
        (AsrProvider ?? Environment.GetEnvironmentVariable("FLOWLOCAL_ASR_PROVIDER"))?.Trim().ToLowerInvariant() ?? "local";

    [JsonIgnore]
    public string EffectiveRewriteProvider =>
        (RewriteProvider ?? Environment.GetEnvironmentVariable("FLOWLOCAL_REWRITE_PROVIDER"))?.Trim().ToLowerInvariant()
        ?? (EffectiveAsrProvider == "assemblyai" ? "assemblyai" : "sotto");
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] KnownModifiers = ["Ctrl", "Alt", "Shift", "Win"];

    private readonly string _path;

    public AppSettingsStore(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal",
            "app-settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
            return Defaults();

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return settings is null ? Defaults() : Normalize(settings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Defaults();
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        AtomicJsonFile.WriteAsync(_path, Normalize(settings), JsonOptions, cancellationToken);

    internal static string? ProtectAssemblyAIApiKey(string? apiKey)
    {
        apiKey = apiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey)) return null;
        if (apiKey.Length > 512 || apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("The API key must contain no spaces or control characters and be at most 512 characters.");
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string? GetAssemblyAIApiKey(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.EncryptedAssemblyAIApiKey))
            return Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY")?.Trim();
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.EncryptedAssemblyAIApiKey), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            // A copied/corrupt credential must not hide Settings or silently use a different key.
            return null;
        }
    }

    internal static AppSettings Defaults() => Normalize(new AppSettings());

    /// <summary>Applies the same validation/clamping used when persisting, for live in-memory use.</summary>
    public AppSettings NormalizeForApply(AppSettings settings) => Normalize(settings);

    private static AppSettings Normalize(AppSettings settings) => new(
        settings.HandsFreeEnabled,
        Math.Clamp(settings.DoubleTapIntervalMilliseconds, 150, 2000),
        NormalizeModifiers(settings.ShortcutModifiers),
        settings.FollowDefaultMicrophone,
        string.IsNullOrWhiteSpace(settings.PreferredMicrophoneDeviceId) ? null : settings.PreferredMicrophoneDeviceId!.Trim(),
        double.IsFinite(settings.OverlayLeft ?? double.NaN) ? settings.OverlayLeft : null,
        double.IsFinite(settings.OverlayTop ?? double.NaN) ? settings.OverlayTop : null,
        settings.Vocabulary,
        settings.RememberedCorrections,
        settings.EncryptedAssemblyAIApiKey,
        settings.AsrProvider?.Trim().ToLowerInvariant(),
        settings.RewriteProvider?.Trim().ToLowerInvariant());

    private static IReadOnlyList<string> NormalizeModifiers(IReadOnlyList<string>? modifiers)
    {
        var selected = (modifiers ?? [])
            .Where(value => KnownModifiers.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(value => KnownModifiers.First(known => known.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => Array.IndexOf(KnownModifiers, value))
            .ToArray();
        return selected.Length == 0 ? AppSettings.DefaultShortcutModifiers : selected;
    }
}
