using System.IO;
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
    double? OverlayTop = null)
{
    public static readonly IReadOnlyList<string> DefaultShortcutModifiers = ["Ctrl", "Win"];
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
        double.IsFinite(settings.OverlayTop ?? double.NaN) ? settings.OverlayTop : null);

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
