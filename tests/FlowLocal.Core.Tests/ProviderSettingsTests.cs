using FlowLocal.App;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class ProviderSettingsTests
{
    [Fact]
    public async Task SavedCredential_RemainsEncryptedAndSurvivesReloadAndRemoval()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"FlowLocal.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        const string key = "provider-settings-test-secret";
        try
        {
            var store = new AppSettingsStore(path);
            await store.SaveAsync(new AppSettings(
                EncryptedAssemblyAIApiKey: AppSettingsStore.ProtectAssemblyAIApiKey(key),
                AsrProvider: "assemblyai", RewriteProvider: "assemblyai"));
            Assert.DoesNotContain(key, await File.ReadAllTextAsync(path));

            var loaded = await new AppSettingsStore(path).LoadAsync();
            Assert.Equal(key, AppSettingsStore.GetAssemblyAIApiKey(loaded));
            Assert.DoesNotContain(key, loaded.ToString());
            Assert.Equal("assemblyai", loaded.EffectiveAsrProvider);
            Assert.Equal("assemblyai", loaded.EffectiveRewriteProvider);

            await store.SaveAsync(loaded with { HandsFreeEnabled = true });
            loaded = await new AppSettingsStore(path).LoadAsync();
            Assert.True(loaded.HandsFreeEnabled);
            Assert.Equal(key, AppSettingsStore.GetAssemblyAIApiKey(loaded));

            await store.SaveAsync(loaded with { EncryptedAssemblyAIApiKey = AppSettingsStore.ProtectAssemblyAIApiKey(" ") });
            Assert.Null((await new AppSettingsStore(path).LoadAsync()).EncryptedAssemblyAIApiKey);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SavedSettings_OverrideEnvironmentAndCorruptCredentialDoesNotSilentlyFallBack()
    {
        string[] names = ["ASSEMBLYAI_API_KEY", "FLOWLOCAL_ASR_PROVIDER", "FLOWLOCAL_REWRITE_PROVIDER"];
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "environment-test-secret");
            Environment.SetEnvironmentVariable(names[1], "assemblyai");
            Environment.SetEnvironmentVariable(names[2], "assemblyai");
            var settings = new AppSettings(
                EncryptedAssemblyAIApiKey: AppSettingsStore.ProtectAssemblyAIApiKey("saved-test-secret"),
                AsrProvider: "local", RewriteProvider: "sotto");
            Assert.Equal("local", settings.EffectiveAsrProvider);
            Assert.Equal("sotto", settings.EffectiveRewriteProvider);
            Assert.Equal("saved-test-secret", AppSettingsStore.GetAssemblyAIApiKey(settings));
            Assert.Null(AppSettingsStore.GetAssemblyAIApiKey(settings with { EncryptedAssemblyAIApiKey = "not-base64" }));
            Assert.Null(AppSettingsStore.GetAssemblyAIApiKey(settings with { EncryptedAssemblyAIApiKey = "AQID" }));

            var unsaved = new AppSettings();
            Assert.Equal("assemblyai", unsaved.EffectiveAsrProvider);
            Assert.Equal("assemblyai", unsaved.EffectiveRewriteProvider);
            Assert.Equal("environment-test-secret", AppSettingsStore.GetAssemblyAIApiKey(unsaved));
        }
        finally
        {
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]);
        }
    }
}
