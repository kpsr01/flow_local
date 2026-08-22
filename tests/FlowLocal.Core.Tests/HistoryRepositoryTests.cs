using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class HistoryRepositoryTests
{
    [Fact]
    public async Task PersistsMetadataAndSearchesTranscripts()
    {
        using var temp = new TempDirectory();
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var entry = Entry(DateTimeOffset.UtcNow) with
        {
            RawTranscript = "alpha raw",
            CleanedTranscript = "searchable phrase",
            TargetApplication = "Editor",
            TargetExecutable = "editor.exe",
            Domain = "example.com",
            OutputCategory = OutputContextCategory.Email,
            Style = TranscriptStyleResolver.Resolve(OutputContextCategory.Email),
            AsrModelName = "whisper",
            CleanupModelName = "cleaner",
            AsrDuration = TimeSpan.FromSeconds(1),
            CleanupDuration = TimeSpan.FromSeconds(2),
            InsertionDuration = TimeSpan.FromMilliseconds(30),
            TotalDuration = TimeSpan.FromSeconds(4),
            InsertionMethod = TextInsertionMethod.Direct,
            RetryCount = 2
        };

        await repository.CreateAsync(entry, CancellationToken.None);

        var stored = await repository.GetAsync(entry.Id, CancellationToken.None);
        Assert.Equal(entry, stored);
        Assert.Equal(entry.Id, Assert.Single(await repository.QueryAsync(new HistoryQuery(Search: "phrase"), CancellationToken.None)).Id);
        Assert.Empty(await repository.QueryAsync(new HistoryQuery(Search: "missing"), CancellationToken.None));
    }

    [Fact]
    public async Task RetentionDeletesExpiredTranscriptWhenAudioCanBeDeleted()
    {
        using var temp = new TempDirectory();
        var recordings = Directory.CreateDirectory(Path.Combine(temp.Path, "Recordings"));
        var audio = Path.Combine(recordings.FullName, "expired.wav");
        await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var entry = Entry(DateTimeOffset.UtcNow.AddDays(-31)) with { AudioFilePath = audio };
        await repository.CreateAsync(entry, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new(true, 7, 30), CancellationToken.None);

        await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(await repository.GetAsync(entry.Id, CancellationToken.None));
        Assert.False(File.Exists(audio));
    }

    [Fact]
    public async Task ForeverRetentionPreservesHistoryAndAudio()
    {
        using var temp = new TempDirectory();
        var recordings = Directory.CreateDirectory(Path.Combine(temp.Path, "Recordings"));
        var audio = Path.Combine(recordings.FullName, "forever.wav");
        await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var entry = Entry(DateTimeOffset.UtcNow.AddYears(-10)) with { AudioFilePath = audio };
        await repository.CreateAsync(entry, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new(true, 0, 0), CancellationToken.None);

        await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(entry, await repository.GetAsync(entry.Id, CancellationToken.None));
        Assert.True(File.Exists(audio));
    }

    [Fact]
    public async Task DisabledAudioDeletesRecordingButKeepsTranscriptMetadataForever()
    {
        using var temp = new TempDirectory();
        var recordings = Directory.CreateDirectory(Path.Combine(temp.Path, "Recordings"));
        var audio = Path.Combine(recordings.FullName, "disabled.wav");
        await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var entry = Entry(DateTimeOffset.UtcNow.AddYears(-10)) with
        {
            AudioFilePath = audio,
            RawTranscript = "raw",
            CleanedTranscript = "clean"
        };
        await repository.CreateAsync(entry, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new(false, 0, 0), CancellationToken.None);

        await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var retained = Assert.IsType<HistoryEntry>(await repository.GetAsync(entry.Id, CancellationToken.None));
        Assert.Null(retained.AudioFilePath);
        Assert.Equal(entry.RawTranscript, retained.RawTranscript);
        Assert.Equal(entry.CleanedTranscript, retained.CleanedTranscript);
        Assert.False(File.Exists(audio));
    }

    [Fact]
    public async Task RetentionRedactsExpiredTranscriptWhenAudioDeletionIsUnsafe()
    {
        using var temp = new TempDirectory();
        var outsideAudio = Path.Combine(Path.GetDirectoryName(temp.Path)!, $"{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(outsideAudio, [1, 2, 3]);
        try
        {
            var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
            await repository.InitializeAsync(CancellationToken.None);
            var entry = Entry(DateTimeOffset.UtcNow.AddDays(-31)) with
            {
                AudioFilePath = outsideAudio,
                RawTranscript = "private raw",
                CleanedTranscript = "private clean",
                TargetApplication = "Private App",
                TargetExecutable = "private.exe",
                Domain = "private.example",
                OutputCategory = OutputContextCategory.Email,
                Style = TranscriptStyleResolver.Resolve(OutputContextCategory.Email),
                AsrModelName = "private model",
                CleanupModelName = "private cleaner"
            };
            await repository.CreateAsync(entry, CancellationToken.None);
            await repository.SaveRetentionSettingsAsync(new(true, 7, 30), CancellationToken.None);

            await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            var retained = Assert.IsType<HistoryEntry>(await repository.GetAsync(entry.Id, CancellationToken.None));
            Assert.Null(retained.RawTranscript);
            Assert.Null(retained.CleanedTranscript);
            Assert.Null(retained.TargetApplication);
            Assert.Null(retained.TargetExecutable);
            Assert.Null(retained.Domain);
            Assert.Null(retained.OutputCategory);
            Assert.Null(retained.Style);
            Assert.Null(retained.AsrModelName);
            Assert.Null(retained.CleanupModelName);
            Assert.Equal(outsideAudio, retained.AudioFilePath);
            Assert.True(File.Exists(outsideAudio));
        }
        finally
        {
            File.Delete(outsideAudio);
        }
    }

    [DirectoryLinksFact]
    public async Task RetentionDoesNotDeleteAudioThroughLinkedDirectory()
    {
        using var temp = new TempDirectory();
        var recordings = Directory.CreateDirectory(Path.Combine(temp.Path, "Recordings"));
        using var outside = new TempDirectory();
        var outsideAudio = Path.Combine(outside.Path, "external.wav");
        await File.WriteAllBytesAsync(outsideAudio, [1, 2, 3]);
        var link = Path.Combine(recordings.FullName, "linked");
        Directory.CreateSymbolicLink(link, outside.Path);
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var entry = Entry(DateTimeOffset.UtcNow.AddDays(-31)) with
        {
            AudioFilePath = Path.Combine(link, Path.GetFileName(outsideAudio))
        };
        await repository.CreateAsync(entry, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new(true, 7, 30), CancellationToken.None);

        await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(await repository.GetAsync(entry.Id, CancellationToken.None));
        Assert.True(File.Exists(outsideAudio));
    }

    [DirectoryLinksFact]
    public async Task RetentionDoesNotDeleteAudioThroughLinkedRecordingsRoot()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var outsideAudio = Path.Combine(outside.Path, "external.wav");
        await File.WriteAllBytesAsync(outsideAudio, [1, 2, 3]);
        Directory.CreateSymbolicLink(Path.Combine(temp.Path, "Recordings"), outside.Path);
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var entry = Entry(DateTimeOffset.UtcNow.AddDays(-31)) with
        {
            AudioFilePath = Path.Combine(temp.Path, "Recordings", Path.GetFileName(outsideAudio))
        };
        await repository.CreateAsync(entry, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new(true, 7, 30), CancellationToken.None);

        await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(await repository.GetAsync(entry.Id, CancellationToken.None));
        Assert.True(File.Exists(outsideAudio));
    }

    [Fact]
    public async Task RetentionKeepsEntriesAtTheCutoffAndRemovesOlderEntries()
    {
        using var temp = new TempDirectory();
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var atCutoff = Entry(now.AddDays(-30));
        var expired = Entry(now.AddDays(-30).AddTicks(-1));
        await repository.CreateAsync(atCutoff, CancellationToken.None);
        await repository.CreateAsync(expired, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new(false, 7, 30), CancellationToken.None);

        await repository.ApplyRetentionAsync(now, CancellationToken.None);

        Assert.NotNull(await repository.GetAsync(atCutoff.Id, CancellationToken.None));
        Assert.Null(await repository.GetAsync(expired.Id, CancellationToken.None));
    }

    [Fact]
    public async Task QueryAppliesFailureApplicationAndPagingFiltersTogether()
    {
        using var temp = new TempDirectory();
        var repository = new SqliteHistoryRepository(Path.Combine(temp.Path, "history.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var createdAt = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var olderMatch = Entry(createdAt) with
        {
            TargetApplication = "Editor",
            State = RecordingState.Failed,
            ErrorCode = DictationErrorCode.AsrFailed
        };
        var newerMatch = olderMatch with { Id = Guid.NewGuid(), CreatedAt = createdAt.AddMinutes(1) };
        var completed = olderMatch with { Id = Guid.NewGuid(), CreatedAt = createdAt.AddMinutes(2), State = RecordingState.Completed };
        await repository.CreateAsync(olderMatch, CancellationToken.None);
        await repository.CreateAsync(newerMatch, CancellationToken.None);
        await repository.CreateAsync(completed, CancellationToken.None);

        var page = await repository.QueryAsync(
            new HistoryQuery(FailedOnly: true, Application: "editor", Limit: 1, Offset: 1),
            CancellationToken.None);

        Assert.Equal(olderMatch.Id, Assert.Single(page).Id);
    }

    private sealed class DirectoryLinksFactAttribute : FactAttribute
    {
        public DirectoryLinksFactAttribute()
        {
            using var target = new TempDirectory();
            var link = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlowLocal.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateSymbolicLink(link, target.Path);
                Directory.Delete(link);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException &&
                                              OperatingSystem.IsWindows())
            {
                Skip = $"Directory links are unavailable: {exception.Message}";
            }
        }
    }

    private static HistoryEntry Entry(DateTimeOffset createdAt) => new(
        Guid.NewGuid(), createdAt, createdAt, createdAt.AddSeconds(3), TimeSpan.FromSeconds(3),
        "raw", "cleaned", null, null, null, null, null, null, null, null,
        null, null, null, null, null, RecordingState.Completed);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlowLocal.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
