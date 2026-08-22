using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class OverrideSettingsFlowTests
{
    [Fact]
    public async Task AddRemoveTogglesAndUniversalFallback_PersistAcrossReloads()
    {
        using var temp = new TempSettings();
        var store = new JsonStyleOverrideStore(temp.Path);
        var domain = TranscriptStyleResolver.Resolve(OutputContextCategory.Email);
        var executable = TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal);
        var universal = TranscriptStyleResolver.Resolve(OutputContextCategory.Document);

        await store.SaveAsync(new OutputStyleSettings(
            StyleClassificationEnabled: false,
            WebsiteDetectionEnabled: false,
            UniversalDefaultCategory: OutputContextCategory.Document,
            UniversalDefaultStyle: universal,
            DomainOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["https://MAIL.Example.com/private/message?id=secret"] = new(OutputContextCategory.Email, domain)
            },
            ExecutableOverrides: new Dictionary<string, OutputStyleOverride>
            {
                [@"C:\Apps\PWSH.EXE"] = new(OutputContextCategory.Terminal, executable)
            }), default);

        var reloaded = await new JsonStyleOverrideStore(temp.Path).LoadAsync(default);
        Assert.False(reloaded.Settings.StyleClassificationEnabled);
        Assert.False(reloaded.Settings.WebsiteDetectionEnabled);
        Assert.Equal(OutputContextCategory.Document, reloaded.Settings.UniversalDefaultCategory);
        Assert.Equal(universal, reloaded.Settings.UniversalDefaultStyle);
        Assert.Equal(new OutputStyleOverride(OutputContextCategory.Email, domain), reloaded.Settings.DomainOverrides!["mail.example.com"]);
        Assert.Equal(new OutputStyleOverride(OutputContextCategory.Terminal, executable), reloaded.Settings.ExecutableOverrides!["pwsh"]);

        await store.SaveAsync(reloaded.Settings with
        {
            DomainOverrides = new Dictionary<string, OutputStyleOverride>(),
            ExecutableOverrides = new Dictionary<string, OutputStyleOverride>()
        }, default);

        var removed = await new JsonStyleOverrideStore(temp.Path).LoadAsync(default);
        Assert.Empty(removed.Settings.DomainOverrides!);
        Assert.Empty(removed.Settings.ExecutableOverrides!);
        Assert.False(removed.Settings.StyleClassificationEnabled);
        Assert.Equal(universal, removed.Settings.UniversalDefaultStyle);
    }

    [Fact]
    public async Task Reset_PersistsDefaultsAcrossReload()
    {
        using var temp = new TempSettings();
        var store = new JsonStyleOverrideStore(temp.Path);
        await store.SaveAsync(new OutputStyleSettings(
            StyleClassificationEnabled: false,
            WebsiteDetectionEnabled: false,
            UniversalDefaultCategory: OutputContextCategory.AiChat,
            UniversalDefaultStyle: TranscriptStyleResolver.Resolve(OutputContextCategory.AiChat),
            DomainOverrides: new Dictionary<string, OutputStyleOverride>
            {
                ["example.com"] = new(OutputContextCategory.Email, TranscriptStyleResolver.Resolve(OutputContextCategory.Email))
            }), default);

        await store.ResetAsync(default);
        var reloaded = await new JsonStyleOverrideStore(temp.Path).LoadAsync(default);

        Assert.True(reloaded.Settings.StyleClassificationEnabled);
        Assert.True(reloaded.Settings.WebsiteDetectionEnabled);
        Assert.Equal(OutputContextCategory.General, reloaded.Settings.UniversalDefaultCategory);
        Assert.Null(reloaded.Settings.UniversalDefaultStyle);
        Assert.Empty(reloaded.Settings.DomainOverrides!);
        Assert.Empty(reloaded.Settings.ExecutableOverrides!);
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
