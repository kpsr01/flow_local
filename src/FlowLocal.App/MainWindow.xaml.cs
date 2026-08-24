using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FlowLocal.Core;

namespace FlowLocal.App;

public enum HistoryAction
{
    CopyRaw,
    CopyCleaned,
    Paste,
    RetryAsr,
    RetryCleanup,
    RetryInsertion,
    Play,
    Export,
    OpenLocation,
    Delete
}

public enum RecoveryChoice { Recover, Delete, Later }

public sealed record NavEntry(string Key, string Title, string Subtitle, string Glyph);

public partial class MainWindow : Window
{
    private static readonly NavEntry[] NavEntries =
    [
        new("general", "General", "Recording behaviour and hands-free activation.", "\uE713"),
        new("shortcuts", "Shortcuts", "Choose the modifiers that activate push-to-talk.", "\uE765"),
        new("microphone", "Microphone", "Follow the Windows default input or pin a device.", "\uE720"),
        new("styles", "Application styles", "Detect the current target and shape its writing style.", "\uE790"),
        new("privacy", "History and privacy", "Everything stays on this machine until you delete it.", "\uE81C"),
        new("diagnostics", "Models and diagnostics", "Live status of the local speech stack.", "\uE9D9")
    ];

    private IStyleOverrideStore? _store;
    private Func<ApplicationContext?>? _getContext;
    private Func<OutputClassification?>? _getClassification;
    private Func<CancellationToken, Task>? _testCurrentTarget;
    private IHistoryRepository? _historyRepository;
    private Func<HistoryAction, HistoryEntry, CancellationToken, Task>? _historyAction;
    private OutputStyleSettings _settings = new();
    private HistoryRetentionSettings _retention = new();
    private bool _loading = true;
    private bool _historyBusy;
    private AppSettingsStore? _appSettings;
    private GlobalShortcutService? _shortcut;
    private WasapiAudioCaptureService? _audio;
    private MoonshineAsrService? _asr;
    private MumbleTranscriptCleaner? _cleaner;
    private Action<AppSettings>? _applyAppSettings;
    private IReadOnlyList<MicrophoneDeviceInfo> _microphones = [];

    public MainWindow()
    {
        ThemeLoader.EnsureTheme(Resources);
        InitializeComponent();
        var categories = Enum.GetValues<OutputContextCategory>();
        FallbackCategoryComboBox.ItemsSource = categories;
        OverrideCategoryComboBox.ItemsSource = categories;
        OverrideCategoryComboBox.SelectedItem = OutputContextCategory.General;
        AudioRetentionComboBox.ItemsSource = RetentionOptions;
        AudioRetentionComboBox.SelectedValuePath = nameof(RetentionOption.Days);
        TranscriptRetentionComboBox.ItemsSource = RetentionOptions;
        TranscriptRetentionComboBox.SelectedValuePath = nameof(RetentionOption.Days);
        NavList.ItemsSource = NavEntries;
        NavList.SelectedIndex = 0;
        NavVersion.Text = $"v{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"}";
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is NavEntry entry)
        {
            Navigate(entry.Key);
        }
    }

    public void Navigate(string key)
    {
        var entry = NavEntries.FirstOrDefault(candidate => candidate.Key == key) ?? NavEntries[0];
        PageGeneral.Visibility = entry.Key == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = entry.Key == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        PageMicrophone.Visibility = entry.Key == "microphone" ? Visibility.Visible : Visibility.Collapsed;
        PageStyles.Visibility = entry.Key == "styles" ? Visibility.Visible : Visibility.Collapsed;
        PagePrivacy.Visibility = entry.Key == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        PageDiagnostics.Visibility = entry.Key == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = entry.Title;
        PageSubtitle.Text = entry.Subtitle;
        if (NavList.SelectedItem is not NavEntry selected || selected.Key != entry.Key)
        {
            NavList.SelectedItem = NavEntries.First(candidate => candidate.Key == entry.Key);
        }
    }

    public async void ConfigureApplicationStyles(IStyleOverrideStore store, Func<ApplicationContext?> getContext,
        Func<OutputClassification?> getClassification, Func<CancellationToken, Task> testCurrentTarget)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _getContext = getContext ?? throw new ArgumentNullException(nameof(getContext));
        _getClassification = getClassification ?? throw new ArgumentNullException(nameof(getClassification));
        _testCurrentTarget = testCurrentTarget ?? throw new ArgumentNullException(nameof(testCurrentTarget));
        await LoadSettingsAsync();
        RefreshDiagnostics();
    }

    public async Task ConfigureHistoryAsync(IHistoryRepository repository,
        Func<HistoryAction, HistoryEntry, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        _historyRepository = repository ?? throw new ArgumentNullException(nameof(repository));
        _historyAction = action ?? throw new ArgumentNullException(nameof(action));
        await RunHistoryAsync("Loading history…", "History loaded.", async token =>
        {
            _retention = await repository.LoadRetentionSettingsAsync(token);
            _loading = true;
            SaveAudioCheckBox.IsChecked = _retention.SaveAudio;
            AudioRetentionComboBox.SelectedValue = _retention.AudioRetentionDays;
            TranscriptRetentionComboBox.SelectedValue = _retention.TranscriptRetentionDays;
            _loading = false;
            await RefreshHistoryAsync(token);
        }, cancellationToken);
    }

    public async Task ConfigureRuntimeAsync(
        AppSettingsStore appSettings,
        GlobalShortcutService shortcut,
        WasapiAudioCaptureService audio,
        MoonshineAsrService asr,
        MumbleTranscriptCleaner cleaner,
        Action<AppSettings> applyAppSettings)
    {
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _shortcut = shortcut ?? throw new ArgumentNullException(nameof(shortcut));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        _applyAppSettings = applyAppSettings ?? throw new ArgumentNullException(nameof(applyAppSettings));

        var settings = await appSettings.LoadAsync();
        _loading = true;
        try
        {
            DoubleTapCheckBox.IsChecked = settings.HandsFreeEnabled;
            DoubleTapIntervalTextBox.Text = settings.DoubleTapIntervalMilliseconds.ToString();
            foreach (var name in settings.ShortcutModifiers ?? [])
            {
                switch (name)
                {
                    case "Ctrl": ModifierCtrlCheckBox.IsChecked = true; break;
                    case "Alt": ModifierAltCheckBox.IsChecked = true; break;
                    case "Shift": ModifierShiftCheckBox.IsChecked = true; break;
                    case "Win": ModifierWinCheckBox.IsChecked = true; break;
                }
            }
            FollowDefaultMicCheckBox.IsChecked = settings.FollowDefaultMicrophone;
            MicDeviceComboBox.DisplayMemberPath = nameof(MicrophoneDeviceInfo.Name);
            MicDeviceComboBox.SelectedValuePath = nameof(MicrophoneDeviceInfo.Id);
            await PopulateMicrophonesAsync(settings.PreferredMicrophoneDeviceId);
            MicDeviceComboBox.IsEnabled = settings.FollowDefaultMicrophone == false;
            ShortcutStatusText.Text = $"Push-to-talk chord: {FormatChord(settings.ShortcutModifiers)}.";
            GeneralStatusText.Text = "General settings loaded.";        }
        finally { _loading = false; }
        RefreshRuntimeDiagnostics();
    }

    private async Task PopulateMicrophonesAsync(string? selectedId)
    {
        if (_audio is null) return;
        try
        {
            _microphones = WasapiAudioCaptureService.ListCaptureDevices();
            MicDeviceComboBox.ItemsSource = _microphones;
            if (selectedId is not null && _microphones.Any(device => device.Id == selectedId))
            {
                MicDeviceComboBox.SelectedValue = selectedId;
            }
            else
            {
                MicDeviceComboBox.SelectedIndex = -1;
                MicrophoneStatusText.Text = _microphones.Count == 0
                    ? "No active recording devices were found."
                    : "No pinned device; the Windows default input device is used.";
            }
        }
        catch (Exception exception)
        {
            MicrophoneStatusText.Text = $"Devices could not be listed: {exception.Message}";
        }
        await Task.CompletedTask;
    }

    private async void SaveGeneral_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _appSettings is null || !int.TryParse(DoubleTapIntervalTextBox.Text.Trim(), out var interval))
        {
            GeneralStatusText.Text = "Enter the double-tap window as a whole number of milliseconds.";
            return;
        }

        interval = Math.Clamp(interval, 150, 2000);
        DoubleTapIntervalTextBox.Text = interval.ToString();
        await SaveAppSettingsAsync(BuildAppSettingsFromUi());
        GeneralStatusText.Text = "General settings saved.";
    }

    private async void ApplyChord_Click(object sender, RoutedEventArgs e) => await ApplyChordAsync();

    private async void ResetChord_Click(object sender, RoutedEventArgs e)
    {
        ModifierCtrlCheckBox.IsChecked = true;
        ModifierAltCheckBox.IsChecked = false;
        ModifierShiftCheckBox.IsChecked = false;
        ModifierWinCheckBox.IsChecked = true;
        await ApplyChordAsync();
    }

    private async Task ApplyChordAsync()
    {
        if (_loading || _appSettings is null) return;
        var chord = CurrentChordFromUi().ToArray();
        if (chord.Length == 0)
        {
            ShortcutStatusText.Text = "Select at least one modifier for the push-to-talk chord.";
            return;
        }

        await SaveAppSettingsAsync(BuildAppSettingsFromUi() with { ShortcutModifiers = chord });
        ShortcutStatusText.Text = $"Push-to-talk chord: {string.Join("+", chord)}.";
    }

    private AppSettings BuildAppSettingsFromUi() => new(
        HandsFreeEnabled: DoubleTapCheckBox.IsChecked == true,
        DoubleTapIntervalMilliseconds: int.TryParse(DoubleTapIntervalTextBox.Text.Trim(), out var interval) ? interval : 400,
        ShortcutModifiers: CurrentChordFromUi().ToArray(),
        FollowDefaultMicrophone: FollowDefaultMicCheckBox.IsChecked == true,
        PreferredMicrophoneDeviceId: FollowDefaultMicCheckBox.IsChecked == true ? null : MicDeviceComboBox.SelectedValue as string);

    private IEnumerable<string> CurrentChordFromUi()
    {
        if (ModifierCtrlCheckBox.IsChecked == true) yield return "Ctrl";
        if (ModifierAltCheckBox.IsChecked == true) yield return "Alt";
        if (ModifierShiftCheckBox.IsChecked == true) yield return "Shift";
        if (ModifierWinCheckBox.IsChecked == true) yield return "Win";
    }

    private async void MicSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _appSettings is null) return;
        MicDeviceComboBox.IsEnabled = FollowDefaultMicCheckBox.IsChecked == false;
        await SaveAppSettingsAsync(BuildAppSettingsFromUi());
        MicrophoneStatusText.Text = "Microphone preference saved. New dictation sessions use it immediately.";
        RefreshRuntimeDiagnostics();
    }

    private async Task SaveAppSettingsAsync(AppSettings settings)
    {
        if (_appSettings is null) return;
        try
        {
            await _appSettings.SaveAsync(settings);
            _applyAppSettings?.Invoke(_appSettings.NormalizeForApply(settings));
            RefreshRuntimeDiagnostics();
        }
        catch (Exception exception)
        {
            GeneralStatusText.Text = $"Settings could not be saved: {exception.Message}";
        }
    }

    private async void RefreshMicDevices_Click(object sender, RoutedEventArgs e)
    {
        var selectedId = MicDeviceComboBox.SelectedValue as string;
        await PopulateMicrophonesAsync(selectedId);
        MicrophoneStatusText.Text = "Recording devices refreshed.";
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MicrophoneStatusText.Text = $"Windows sound settings could not be opened: {exception.Message}";
        }
    }

    public void RefreshRuntimeDiagnostics()
    {        VersionText.Text = (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()) ?? "—";
        RuntimeText.Text = $".NET {Environment.Version} — {Environment.OSVersion.VersionString}";
        AsrModelText.Text = $"{MoonshineAsrService.ModelName} (Moonshine ONNX)";
        var status = _asr?.Status;
        AsrStateText.Text = status is null ? "—"
            : status.Provider is { Length: > 0 } provider ? $"{status.State} — {provider}" : status.State.ToString();
        CleanupBackendText.Text = _cleaner is null ? "—" :
            $"{_cleaner.DisplayName}{(_cleaner.IsLoaded ? $" — {_cleaner.ExecutionTarget}" : " — not loaded yet")}";
        CleanupPathText.Text = MumbleTranscriptCleaner.ConfiguredModelPath
            ?? "Set FLOWLOCAL_CLEANUP_MODEL_PATH to a local GGUF file.";
        if (_audio is not null && FollowDefaultMicCheckBox is not null)
        {
            MicModeText.Text = FollowDefaultMicCheckBox.IsChecked == true
                ? "Follows the Windows default input device."
                : MicDeviceComboBox.SelectedItem is MicrophoneDeviceInfo pinned ? $"Pinned: {pinned.Name}" : "Pinned device missing; falls back to default.";
        }
    }

    public void ShowHistory(Guid? selectedId = null)
    {
        Show();
        Activate();
        Navigate("privacy");
        HistoryGroupBox.BringIntoView();
        if (selectedId is { } id)
        {
            var item = HistoryListBox.Items.Cast<HistoryItem>().FirstOrDefault(candidate => candidate.Entry.Id == id);
            if (item is not null)
            {
                HistoryListBox.SelectedItem = item;
                HistoryListBox.ScrollIntoView(item);
            }
        }
        HistorySearchTextBox.Focus();
    }

    public async Task RefreshHistoryAsync() =>
        _ = await RunHistoryAsync("Refreshing history…", "History updated.", RefreshHistoryAsync);

    public Task<RecoveryChoice> PromptRecoveryAsync(IReadOnlyList<HistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return Task.FromResult(RecoveryChoice.Later);
        cancellationToken.ThrowIfCancellationRequested();
        var message = $"FlowLocal found {entries.Count} interrupted dictation session{(entries.Count == 1 ? "" : "s")}.\n\n" +
                      "Yes: open History to recover manually\nNo: delete interrupted sessions\nCancel: decide later\n\nRecovery never inserts text automatically.";
        var result = MessageBox.Show(this, message, "Recover interrupted dictation", MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question, MessageBoxResult.Cancel);
        return Task.FromResult(result switch
        {
            MessageBoxResult.Yes => RecoveryChoice.Recover,
            MessageBoxResult.No => RecoveryChoice.Delete,
            _ => RecoveryChoice.Later
        });
    }

    public void RefreshDiagnostics()
    {
        var context = _getContext?.Invoke();
        var classification = _getClassification?.Invoke();
        if (context is null || classification is null) return;
        ExecutableText.Text = Safe(context.ExecutableName);
        ApplicationText.Text = Safe(context.DisplayName);
        DomainText.Text = _settings.WebsiteDetectionEnabled ? SafeDomain(context.Domain) : "Disabled";
        CategoryText.Text = classification.Category.ToString();
        StyleText.Text = $"{classification.Style.Tone} / {classification.Style.Structure}";
        SourceText.Text = classification.Source.ToString();
        RuleText.Text = Safe(classification.Rule);
        ErrorText.Text = Safe(classification.Diagnostic.Error, "None");
    }

    protected override void OnClosing(CancelEventArgs e) { e.Cancel = true; Hide(); }

    private async Task LoadSettingsAsync()
    {
        _loading = true;
        try
        {
            var loaded = await _store!.LoadAsync(CancellationToken.None);
            _settings = loaded.Settings;
            WebsiteDetectionCheckBox.IsChecked = _settings.WebsiteDetectionEnabled;
            StyleClassificationCheckBox.IsChecked = _settings.StyleClassificationEnabled;
            FallbackCategoryComboBox.SelectedItem = _settings.UniversalDefaultCategory;
            SettingsStatusText.Text = loaded.Diagnostic ?? "Settings loaded.";
            RefreshOverrides();
        }
        catch (Exception exception) { SettingsStatusText.Text = $"Settings could not be loaded: {exception.Message}"; }
        finally { _loading = false; }
    }

    private async void SettingsChanged_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _store is null || FallbackCategoryComboBox.SelectedItem is not OutputContextCategory category) return;
        _settings = _settings with
        {
            WebsiteDetectionEnabled = WebsiteDetectionCheckBox.IsChecked == true,
            StyleClassificationEnabled = StyleClassificationCheckBox.IsChecked == true,
            UniversalDefaultCategory = category,
            UniversalDefaultStyle = TranscriptStyleResolver.Resolve(category)
        };
        await SaveSettingsAsync();
        RefreshDiagnostics();
    }

    private async void TestCurrentTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_testCurrentTarget is null) { SettingsStatusText.Text = "Application styles are not available yet."; return; }
        try
        {
            SettingsStatusText.Text = "Testing current target…";
            await _testCurrentTarget(CancellationToken.None);
            RefreshDiagnostics();
            SettingsStatusText.Text = "Current target tested.";
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            SettingsStatusText.Text = "Target detection failed; General style remains available.";
        }
    }

    private async void SaveOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || OverrideCategoryComboBox.SelectedItem is not OutputContextCategory category) return;
        var key = OverrideKeyTextBox.Text.Trim();
        var isDomain = (OverrideKindComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Domain";
        key = isDomain ? NormalizeDomain(key) : NormalizeExecutable(key);
        if (key.Length == 0)
        {
            SettingsStatusText.Text = isDomain ? "Enter a domain only, such as example.com. Full URLs are not accepted." : "Enter an executable name.";
            return;
        }
        var styleOverride = new OutputStyleOverride(category, TranscriptStyleResolver.Resolve(category));
        if (isDomain) { var values = Copy(_settings.DomainOverrides); values[key] = styleOverride; _settings = _settings with { DomainOverrides = values }; }
        else { var values = Copy(_settings.ExecutableOverrides); values[key] = styleOverride; _settings = _settings with { ExecutableOverrides = values }; }
        OverrideKeyTextBox.Clear();
        await SaveSettingsAsync();
        RefreshOverrides();
    }

    private async void RemoveOverride_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null || OverridesListBox.SelectedItem is not OverrideItem selected) return;
        var values = Copy(selected.IsDomain ? _settings.DomainOverrides : _settings.ExecutableOverrides);
        values.Remove(selected.Key);
        _settings = selected.IsDomain ? _settings with { DomainOverrides = values } : _settings with { ExecutableOverrides = values };
        await SaveSettingsAsync();
        RefreshOverrides();
    }

    private async void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        try
        {
            await _store.ResetAsync(CancellationToken.None);
            await LoadSettingsAsync();
            RefreshDiagnostics();
            SettingsStatusText.Text = "Application style settings reset to defaults.";
        }
        catch (Exception exception) { SettingsStatusText.Text = $"Settings could not be reset: {exception.Message}"; }
    }

    private async void HistorySearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await RefreshHistoryFromUiAsync(); }
    }

    private async void HistoryFilterChanged_Click(object sender, RoutedEventArgs e) => await RefreshHistoryFromUiAsync();

    private async Task RefreshHistoryFromUiAsync()
    {
        if (_loading || _historyRepository is null || _historyBusy) return;
        _ = await RunHistoryAsync("Searching history…", "History updated.", RefreshHistoryAsync);
    }

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        var selectedId = (HistoryListBox.SelectedItem as HistoryItem)?.Entry.Id;
        var application = (HistoryApplicationComboBox.SelectedItem as ApplicationFilter)?.Value;
        var entries = await _historyRepository!.QueryAsync(new HistoryQuery(
            HistorySearchTextBox.Text, HistoryFailedOnlyCheckBox.IsChecked == true, application), cancellationToken);
        var items = entries.Select(entry => new HistoryItem(entry)).ToArray();
        HistoryListBox.ItemsSource = items;
        var view = CollectionViewSource.GetDefaultView(HistoryListBox.ItemsSource);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryItem.Day)));
        var applications = entries.Select(entry => entry.TargetApplication).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).Select(value => new ApplicationFilter(value!, value!)).Prepend(new("All applications", null)).ToArray();
        _loading = true;
        HistoryApplicationComboBox.ItemsSource = applications;
        HistoryApplicationComboBox.SelectedItem = applications.FirstOrDefault(item => string.Equals(item.Value, application, StringComparison.OrdinalIgnoreCase)) ?? applications[0];
        _loading = false;
        HistoryListBox.SelectedItem = items.FirstOrDefault(item => item.Entry.Id == selectedId) ?? items.FirstOrDefault();
        if (items.Length == 0) HistoryStatusText.Text = "No history matches these filters.";
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not HistoryItem item)
        {
            HistoryRawTextBox.Clear(); HistoryCleanedTextBox.Clear(); HistoryMetadataText.Text = "Select an entry to see its details."; HistoryActionPanel.IsEnabled = false; return;
        }
        var entry = item.Entry;
        HistoryRawTextBox.Text = entry.RawTranscript ?? "";
        HistoryCleanedTextBox.Text = entry.CleanedTranscript ?? "";
        HistoryMetadataText.Text = $"{entry.CreatedAt.LocalDateTime:g}  •  {entry.State}  •  {Safe(entry.TargetApplication)}  •  {SafeDomain(entry.Domain)}\n" +
            $"Duration {Format(entry.Duration)}  •  ASR {Safe(entry.AsrModelName)} ({Format(entry.AsrDuration)})  •  Cleanup {Safe(entry.CleanupModelName)} ({Format(entry.CleanupDuration)})\n" +
            $"Insertion {entry.InsertionMethod?.ToString() ?? "—"} ({Format(entry.InsertionDuration)})  •  Error {entry.ErrorCode}  •  Retries {entry.RetryCount}";
        HistoryActionPanel.IsEnabled = !_historyBusy;
    }

    private async void HistoryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_historyAction is null || HistoryListBox.SelectedItem is not HistoryItem item || sender is not Button { Tag: string tag } || !Enum.TryParse<HistoryAction>(tag, out var action)) return;
        if (await RunHistoryAsync($"{ActionLabel(action)}…", $"{ActionLabel(action)} completed.", token => _historyAction(action, item.Entry, token)))
            await RefreshHistoryPreservingStatusAsync();
    }

    private async void DeleteSelectedHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRepository is null || HistoryListBox.SelectedItem is not HistoryItem item || MessageBox.Show(this,
            "Delete this history entry and its recording?", "Delete history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await RunHistoryAsync("Deleting history…", "History entry deleted.", token => _historyRepository.DeleteAsync(item.Entry.Id, true, token)))
            await RefreshHistoryPreservingStatusAsync();
    }

    private async void DeleteAllHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRepository is null || MessageBox.Show(this, "Delete all transcript history and recordings? This cannot be undone.",
            "Delete all history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await RunHistoryAsync("Deleting all history…", "All history deleted.", token => _historyRepository.DeleteAllAsync(true, token)))
            await RefreshHistoryPreservingStatusAsync();
    }

    private async void ClearRecordings_Click(object sender, RoutedEventArgs e)
    {
        if (_historyRepository is null || MessageBox.Show(this, "Delete all saved recordings but keep transcript history?",
            "Clear recordings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await RunHistoryAsync("Clearing recordings…", "Recordings cleared.", _historyRepository.ClearRecordingsAsync))
            await RefreshHistoryPreservingStatusAsync();
    }

    private async void RetentionChanged_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _historyRepository is null || AudioRetentionComboBox.SelectedItem is not RetentionOption audio || TranscriptRetentionComboBox.SelectedItem is not RetentionOption transcript) return;
        _retention = new HistoryRetentionSettings(SaveAudioCheckBox.IsChecked == true, audio.Days, transcript.Days);
        _ = await RunHistoryAsync("Applying retention settings…", "Retention settings applied.", async token =>
        {
            await _historyRepository.SaveRetentionSettingsAsync(_retention, token);
            await _historyRepository.ApplyRetentionAsync(DateTimeOffset.UtcNow, token);
            await RefreshHistoryAsync(token);
        });
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowLocal");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            HistoryStatusText.Text = "Opened the local FlowLocal data directory.";
        }
        catch (Exception exception) { HistoryStatusText.Text = $"Data directory could not be opened: {exception.Message}"; }
    }

    private async Task<bool> RunHistoryAsync(string pending, string success, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        if (_historyBusy) return false;
        _historyBusy = true;
        HistoryGroupBox.IsEnabled = false;
        HistoryStatusText.Text = pending;
        try { await operation(cancellationToken); HistoryStatusText.Text = success; return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { HistoryStatusText.Text = "Operation cancelled."; return false; }
        catch (Exception exception) { HistoryStatusText.Text = $"Operation failed: {exception.Message}"; return false; }
        finally { _historyBusy = false; HistoryGroupBox.IsEnabled = true; HistoryActionPanel.IsEnabled = HistoryListBox.SelectedItem is HistoryItem; }
    }

    private async Task RefreshHistoryPreservingStatusAsync()
    {
        var status = HistoryStatusText.Text;
        await RefreshHistoryAsync(CancellationToken.None);
        HistoryStatusText.Text = status;
    }

    private async Task SaveSettingsAsync()
    {
        try { await _store!.SaveAsync(_settings, CancellationToken.None); SettingsStatusText.Text = "Settings saved locally."; }
        catch (Exception exception) { SettingsStatusText.Text = $"Settings could not be saved: {exception.Message}"; }
    }

    private void RefreshOverrides() => OverridesListBox.ItemsSource = (_settings.ExecutableOverrides ?? new Dictionary<string, OutputStyleOverride>())
        .Select(pair => new OverrideItem(false, pair.Key, pair.Value.Category))
        .Concat((_settings.DomainOverrides ?? new Dictionary<string, OutputStyleOverride>()).Select(pair => new OverrideItem(true, pair.Key, pair.Value.Category)))
        .OrderBy(item => item.Kind).ThenBy(item => item.Key).ToArray();

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshRuntimeDiagnostics();

    private static string FormatChord(IReadOnlyList<string>? chord) =>
        chord is null || chord.Count == 0 ? "Ctrl+Win" : string.Join("+", chord);

    private static readonly RetentionOption[] RetentionOptions = [new("1 day", 1), new("7 days", 7), new("30 days", 30), new("90 days", 90), new("Forever", 0)];
    private static string ActionLabel(HistoryAction action) => action switch { HistoryAction.RetryAsr => "Speech recognition retry", HistoryAction.RetryCleanup => "Cleanup retry", HistoryAction.RetryInsertion => "Insertion retry", HistoryAction.Play => "Playback", HistoryAction.Export => "Recording export", HistoryAction.OpenLocation => "Opening recording location", HistoryAction.Paste => "Paste", HistoryAction.Delete => "Delete", _ => "Copy" };
    private static string Format(TimeSpan? value) => value is null ? "—" : value.Value.TotalSeconds < 1 ? $"{value.Value.TotalMilliseconds:0} ms" : $"{value.Value.TotalSeconds:0.0} s";
    private static Dictionary<string, OutputStyleOverride> Copy(IReadOnlyDictionary<string, OutputStyleOverride>? values) => values is null ? new(StringComparer.OrdinalIgnoreCase) : new(values, StringComparer.OrdinalIgnoreCase);
    private static string NormalizeExecutable(string value) => value.Trim().ToLowerInvariant() is { Length: > 0 } executable ? executable.EndsWith(".exe", StringComparison.Ordinal) ? executable : executable + ".exe" : "";
    private static string NormalizeDomain(string value) => value.Contains("://", StringComparison.Ordinal) || value.Contains('/') || value.Contains('\\') ? "" : value.Trim().Trim('.').ToLowerInvariant();
    private static string SafeDomain(string? value) => string.IsNullOrWhiteSpace(value) || value.Contains("://", StringComparison.Ordinal) ? "—" : value;
    private static string Safe(string? value, string fallback = "—") => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed record OverrideItem(bool IsDomain, string Key, OutputContextCategory Category) { public string Kind => IsDomain ? "Domain" : "Executable"; public override string ToString() => $"{Kind}: {Key} → {Category}"; }
    private sealed record RetentionOption(string Label, int Days) { public override string ToString() => Label; }
    private sealed record ApplicationFilter(string Label, string? Value) { public override string ToString() => Label; }
    private sealed record HistoryItem(HistoryEntry Entry)
    {
        public string Day => Entry.CreatedAt.LocalDateTime.ToString("D");
        public string Summary => Entry.CleanedTranscript ?? Entry.RawTranscript ?? "No transcript";
        public string Details => $"{Entry.CreatedAt.LocalDateTime:t}  •  {Safe(Entry.TargetApplication)}  •  {Entry.State}";
    }
}
