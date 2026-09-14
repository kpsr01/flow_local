using FlowLocal.App;

namespace FlowLocal.Core.Tests;

public sealed class SessionAudioRecoveryTests
{
    [Fact]
    public void CreateRecordingPath_UsesCanonicalSessionPath_AndSuccessfulRecordingIsRetained()
    {
        var sessionId = Guid.NewGuid();
        var path = AsrRetryService.CreateRecordingPath(sessionId);

        try
        {
            using (var wave = new PcmWaveFile(path))
                wave.Write([0, 1, 2, 3]);

            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlowLocal",
                    "Recordings",
                    $"{sessionId:N}.wav"),
                path);
            Assert.True(File.Exists(path));
            Assert.Equal([0, 1, 2, 3], PcmWaveFile.ReadData(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeleteRecording_RemovesCancelledSessionRecording()
    {
        var path = AsrRetryService.CreateRecordingPath(Guid.NewGuid());
        using (var wave = new PcmWaveFile(path))
            wave.Write([4, 5, 6, 7]);

        AsrRetryService.DeleteRecording(path);

        Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task RecoveryTerminalizesInterruptedSessionSoItIsReportedOnlyOnce()
    {
        using var temp = new TempDirectory();
        var audio = Path.Combine(temp.Path, "Recordings", $"{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        using (var wave = new PcmWaveFile(audio))
            wave.Write([4, 5, 6, 7]);

        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var entry = new HistoryEntry(
            Guid.NewGuid(), now, now, null, null, "raw", "cleaned", audio,
            "Terminal", "windowsterminal", null, OutputContextCategory.Terminal,
            TranscriptStyleResolver.Resolve(OutputContextCategory.Terminal), "asr", "cleaner",
            null, null, null, null, null, RecordingState.Inserting);
        await repository.CreateAsync(entry, CancellationToken.None);
        var recovery = new CrashRecoveryService(repository);

        Assert.Single(await recovery.ScanAsync());
        Assert.Empty(await recovery.ScanAsync());
        var stored = await repository.GetAsync(entry.Id, CancellationToken.None);
        Assert.Equal(RecordingState.Failed, stored?.State);
        Assert.Equal(DictationErrorCode.Interrupted, stored?.ErrorCode);
        Assert.Equal("cleaned", stored?.CleanedTranscript);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlowLocal.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
