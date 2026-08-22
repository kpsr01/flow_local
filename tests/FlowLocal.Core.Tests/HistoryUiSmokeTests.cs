using System.Windows.Threading;
using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

[Collection("UiSerial")]
public sealed class HistoryUiSmokeTests
{
    [Fact]
    public Task ShowsRecoverableHistoryWithoutInvokingActions() => RunStaAsync(async () =>
    {
        var entry = new HistoryEntry(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero),
            DateTimeOffset.UtcNow.AddSeconds(-5),
            null,
            TimeSpan.FromSeconds(5),
            "raw smoke transcript",
            "clean smoke transcript",
            null,
            "Smoke Editor",
            "smoke.exe",
            "example.test",
            OutputContextCategory.Email,
            TranscriptStyleResolver.Resolve(OutputContextCategory.Email),
            "smoke-asr",
            "smoke-cleaner",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(250),
            null,
            TimeSpan.FromSeconds(5),
            null,
            RecordingState.Failed,
            DictationErrorCode.Interrupted,
            2);
        var repository = new InMemoryHistoryRepository(entry);
        var actionCalls = 0;
        var window = new MainWindow();

        try
        {
            await window.ConfigureHistoryAsync(repository, (_, _, _) =>
            {
                actionCalls++;
                return Task.CompletedTask;
            });
            window.ShowHistory(entry.Id);

            Assert.True(window.IsVisible);
            Assert.Single(window.HistoryListBox.Items);
            Assert.NotNull(window.HistoryListBox.SelectedItem);
            Assert.Equal(entry.RawTranscript, window.HistoryRawTextBox.Text);
            Assert.Equal(entry.CleanedTranscript, window.HistoryCleanedTextBox.Text);
            Assert.Contains("Smoke Editor", window.HistoryMetadataText.Text);
            Assert.Contains("example.test", window.HistoryMetadataText.Text);
            Assert.Contains("smoke-asr", window.HistoryMetadataText.Text);
            Assert.Contains("smoke-cleaner", window.HistoryMetadataText.Text);
            Assert.Contains("Interrupted", window.HistoryMetadataText.Text);
            Assert.Equal("History loaded.", window.HistoryStatusText.Text);
            Assert.Equal(0, actionCalls);
        }
        finally
        {
            window.Hide();
        }
    });

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception exception) { error = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
            if (error is null) completion.SetResult();
            else completion.SetException(error);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private sealed class InMemoryHistoryRepository(HistoryEntry entry) : IHistoryRepository
    {
        private readonly HistoryEntry _entry = entry;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CreateAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateAsync(HistoryEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<HistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == _entry.Id ? _entry : null);
        public Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistoryEntry>>([_entry]);
        public Task<IReadOnlyList<HistoryEntry>> GetRecoverableAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistoryEntry>>([_entry]);
        public Task DeleteAsync(Guid id, bool deleteAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAllAsync(bool deleteAudio, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearRecordingsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<HistoryRetentionSettings> LoadRetentionSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(new HistoryRetentionSettings());
        public Task SaveRetentionSettingsAsync(HistoryRetentionSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
