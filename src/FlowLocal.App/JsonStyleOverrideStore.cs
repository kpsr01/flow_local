using FlowLocal.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace FlowLocal.App;

public sealed class JsonStyleOverrideStore : IStyleOverrideStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public JsonStyleOverrideStore(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal",
            "application-styles.json");

    public async Task<StyleOverrideLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
            return new StyleOverrideLoadResult(Defaults(), "Style override settings file was not found; defaults are active.");

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<OutputStyleSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return settings is null
                ? new StyleOverrideLoadResult(Defaults(), "Style override settings were empty; defaults are active.")
                : new StyleOverrideLoadResult(Normalize(settings));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new StyleOverrideLoadResult(Defaults(), $"Style override settings could not be loaded; defaults are active. {exception.Message}");
        }
    }

    public Task SaveAsync(OutputStyleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return AtomicJsonFile.WriteAsync(_path, Normalize(settings), JsonOptions, cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(_path, Defaults(), JsonOptions, cancellationToken);

    private static OutputStyleSettings Defaults() => new();

    private static OutputStyleSettings Normalize(OutputStyleSettings settings) => settings with
    {
        DomainOverrides = NormalizeDomains(settings.DomainOverrides),
        ExecutableOverrides = NormalizeExecutables(settings.ExecutableOverrides)
    };

    private static IReadOnlyDictionary<string, OutputStyleOverride> NormalizeDomains(
        IReadOnlyDictionary<string, OutputStyleOverride>? overrides)
    {
        var normalized = new SortedDictionary<string, OutputStyleOverride>(StringComparer.Ordinal);
        if (overrides is null)
            return normalized;

        foreach (var (key, value) in overrides)
        {
            var domain = DomainNormalizer.TryNormalize(key) ?? DomainNormalizer.TryNormalize($"https://{key.Trim()}");
            if (domain is not null && value is not null)
                normalized[domain] = value;
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, OutputStyleOverride> NormalizeExecutables(
        IReadOnlyDictionary<string, OutputStyleOverride>? overrides)
    {
        var normalized = new SortedDictionary<string, OutputStyleOverride>(StringComparer.Ordinal);
        if (overrides is null)
            return normalized;

        foreach (var (key, value) in overrides)
        {
            var executable = ApplicationNameCatalog.Normalize(key).ExecutableName;
            if (executable.Length > 0 && value is not null)
                normalized[executable] = value;
        }

        return normalized;
    }
}
