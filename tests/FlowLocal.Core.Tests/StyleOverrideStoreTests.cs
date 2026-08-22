using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class StyleOverrideStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSettingsAndNormalizesOverrideKeys()
    {
        using var temp = new TempSettings();
        var store = new JsonStyleOverrideStore(temp.Path);
        var domainStyle = TranscriptStyleResolver.Resolve(OutputContextCategory.WorkMessaging);
        var executableStyle = TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal);
        var settings = new OutputStyleSettings(
            StyleClassificationEnabled: false,
            WebsiteDetectionEnabled: false,
            UniversalDefaultCategory: OutputContextCategory.Document,
            UniversalDefaultStyle: TranscriptStyleResolver.Resolve(OutputContextCategory.Document),
            DomainOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["HTTPS://Team.SLACK.COM/path"] = new(OutputContextCategory.WorkMessaging, domainStyle)
            },
            ExecutableOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["C:\\Program Files\\PowerShell\\PWSH.EXE"] = new(OutputContextCategory.Terminal, executableStyle)
            });

        await store.SaveAsync(settings, default);
        var loaded = await store.LoadAsync(default);

        Assert.Null(loaded.Diagnostic);
        Assert.False(loaded.Settings.StyleClassificationEnabled);
        Assert.False(loaded.Settings.WebsiteDetectionEnabled);
        Assert.Equal(OutputContextCategory.Document, loaded.Settings.UniversalDefaultCategory);
        Assert.Equal(settings.UniversalDefaultStyle, loaded.Settings.UniversalDefaultStyle);
        Assert.Equal(new OutputStyleOverride(OutputContextCategory.WorkMessaging, domainStyle), loaded.Settings.DomainOverrides!["team.slack.com"]);
        Assert.Equal(new OutputStyleOverride(OutputContextCategory.Terminal, executableStyle), loaded.Settings.ExecutableOverrides!["pwsh"]);
        Assert.Contains("\"WorkMessaging\"", await File.ReadAllTextAsync(temp.Path));
    }

    [Fact]
    public async Task Reset_ReplacesSavedOverridesWithDefaults()
    {
        using var temp = new TempSettings();
        var store = new JsonStyleOverrideStore(temp.Path);
        await store.SaveAsync(
            new OutputStyleSettings(
                StyleClassificationEnabled: false,
                DomainOverrides: new Dictionary<string, OutputStyleOverride>
                {
                    ["example.com"] = new(OutputContextCategory.Email, TranscriptStyleResolver.Resolve(OutputContextCategory.Email))
                }),
            default);

        await store.ResetAsync(default);
        var loaded = await store.LoadAsync(default);

        Assert.Null(loaded.Diagnostic);
        Assert.True(loaded.Settings.StyleClassificationEnabled);
        Assert.True(loaded.Settings.WebsiteDetectionEnabled);
        Assert.Equal(OutputContextCategory.General, loaded.Settings.UniversalDefaultCategory);
        Assert.Null(loaded.Settings.UniversalDefaultStyle);
        Assert.Empty(loaded.Settings.DomainOverrides!);
        Assert.Empty(loaded.Settings.ExecutableOverrides!);
    }

    [Fact]
    public async Task Load_CorruptJsonFallsBackToDefaultsWithDiagnostic()
    {
        using var temp = new TempSettings();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temp.Path)!);
        await File.WriteAllTextAsync(temp.Path, "{ not json");

        var loaded = await new JsonStyleOverrideStore(temp.Path).LoadAsync(default);

        Assert.True(loaded.Settings.StyleClassificationEnabled);
        Assert.True(loaded.Settings.WebsiteDetectionEnabled);
        Assert.Equal(OutputContextCategory.General, loaded.Settings.UniversalDefaultCategory);
        Assert.NotNull(loaded.Diagnostic);
        Assert.Contains("defaults are active", loaded.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_AtomicallyReplacesExistingFileWithoutLeavingTemporaryFiles()
    {
        using var temp = new TempSettings();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temp.Path)!);
        await File.WriteAllTextAsync(temp.Path, "old content");
        var store = new JsonStyleOverrideStore(temp.Path);
        var expected = new OutputStyleSettings(UniversalDefaultCategory: OutputContextCategory.AiChat);

        await store.SaveAsync(expected, default);
        var loaded = await store.LoadAsync(default);

        Assert.Equal(OutputContextCategory.AiChat, loaded.Settings.UniversalDefaultCategory);
        Assert.DoesNotContain("old content", await File.ReadAllTextAsync(temp.Path));
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(temp.Path)!, "*.tmp"));
    }

    [Fact]
    public async Task Load_MissingFileUsesDefaultsWithoutCreatingAFile()
    {
        using var temp = new TempSettings();

        var loaded = await new JsonStyleOverrideStore(temp.Path).LoadAsync(default);

        Assert.True(loaded.Settings.StyleClassificationEnabled);
        Assert.True(loaded.Settings.WebsiteDetectionEnabled);
        Assert.Equal(OutputContextCategory.General, loaded.Settings.UniversalDefaultCategory);
        Assert.Null(loaded.Settings.UniversalDefaultStyle);
        Assert.NotNull(loaded.Diagnostic);
        Assert.False(File.Exists(temp.Path));
    }

    private sealed class TempSettings : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FlowLocal.Tests.{Guid.NewGuid():N}");

        public string Path => System.IO.Path.Combine(directory, "settings.json");

        public void Dispose()
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
