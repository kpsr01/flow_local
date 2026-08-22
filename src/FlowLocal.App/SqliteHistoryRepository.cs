using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using FlowLocal.Core;
using Microsoft.Data.Sqlite;

namespace FlowLocal.App;

public sealed class SqliteHistoryRepository : IHistoryRepository
{
    private const string DateFormat = "O";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath;
    private readonly string _recordingsRoot;

    public SqliteHistoryRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal", "flowlocal.db");
        _recordingsRoot = Path.GetFullPath(Path.Combine(
            databasePath is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
            databasePath is null ? Path.Combine("FlowLocal", "Recordings") : "Recordings"));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                recording_started_at TEXT,
                recording_ended_at TEXT,
                duration_ticks INTEGER,
                raw_transcript TEXT,
                cleaned_transcript TEXT,
                audio_file_path TEXT,
                target_application TEXT,
                target_executable TEXT,
                domain TEXT,
                output_category TEXT,
                style_json TEXT,
                asr_model_name TEXT,
                cleanup_model_name TEXT,
                asr_duration_ticks INTEGER,
                cleanup_duration_ticks INTEGER,
                insertion_duration_ticks INTEGER,
                total_duration_ticks INTEGER,
                insertion_method TEXT,
                state TEXT NOT NULL,
                error_code TEXT NOT NULL,
                retry_count INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_created_at ON history(created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_history_application ON history(target_application);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task CreateAsync(HistoryEntry entry, CancellationToken cancellationToken) => SaveAsync(entry, false, cancellationToken);

    public Task UpdateAsync(HistoryEntry entry, CancellationToken cancellationToken) => SaveAsync(entry, true, cancellationToken);

    public async Task<HistoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM history WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
    }

    public async Task<IReadOnlyList<HistoryEntry>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add("(raw_transcript LIKE $search ESCAPE '\\' OR cleaned_transcript LIKE $search ESCAPE '\\')");
            command.Parameters.AddWithValue("$search", $"%{EscapeLike(query.Search.Trim())}%");
        }
        if (query.FailedOnly)
            conditions.Add("state = $failed");
        if (!string.IsNullOrWhiteSpace(query.Application))
        {
            conditions.Add("target_application = $application COLLATE NOCASE");
            command.Parameters.AddWithValue("$application", query.Application.Trim());
        }
        command.Parameters.AddWithValue("$failed", Stable(RecordingState.Failed));
        command.CommandText = $"SELECT {Columns} FROM history" +
            (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions)) +
            " ORDER BY created_at DESC, id DESC" +
            (query.Limit is > 0 ? " LIMIT $limit OFFSET $offset" : "");
        if (query.Limit is > 0)
        {
            command.Parameters.AddWithValue("$limit", query.Limit.Value);
            command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset ?? 0));
        }
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetRecoverableAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM history WHERE state NOT IN ($completed, $cancelled, $failed) ORDER BY created_at";
        command.Parameters.AddWithValue("$completed", Stable(RecordingState.Completed));
        command.Parameters.AddWithValue("$cancelled", Stable(RecordingState.Cancelled));
        command.Parameters.AddWithValue("$failed", Stable(RecordingState.Failed));
        return await ReadManyAsync(command, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, bool deleteAudio, CancellationToken cancellationToken)
    {
        var entry = deleteAudio ? await GetAsync(id, cancellationToken) : null;
        if (entry is not null && !DeleteAudio(entry.AudioFilePath))
            throw new IOException("The recording could not be deleted. The history entry was kept so deletion can be retried.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAllAsync(bool deleteAudio, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!deleteAudio)
        {
            await using var deleteAll = connection.CreateCommand();
            deleteAll.CommandText = "DELETE FROM history";
            await deleteAll.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, audio_file_path FROM history";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var deletableIds = new List<string>();
        var failures = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1) || DeleteAudio(reader.GetString(1)))
                deletableIds.Add(reader.GetString(0));
            else
                failures++;
        }
        await reader.DisposeAsync();
        foreach (var id in deletableIds)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM history WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        if (failures > 0)
            throw new IOException($"{failures} recording file{(failures == 1 ? "" : "s")} could not be deleted; their history entries were kept.");
    }

    public async Task ClearRecordingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, audio_file_path FROM history WHERE audio_file_path IS NOT NULL";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var clearedIds = new List<string>();
        var failures = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (DeleteAudio(reader.GetString(1)))
                clearedIds.Add(reader.GetString(0));
            else
                failures++;
        }
        await reader.DisposeAsync();
        foreach (var id in clearedIds)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE history SET audio_file_path = NULL WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (failures > 0)
            throw new IOException($"{failures} recording file{(failures == 1 ? "" : "s")} could not be deleted; their paths were kept for retry.");
    }

    public async Task<HistoryRetentionSettings> LoadRetentionSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'history_retention'";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is null ? new HistoryRetentionSettings() :
            JsonSerializer.Deserialize<HistoryRetentionSettings>(value, JsonOptions) ?? new HistoryRetentionSettings();
    }

    public async Task SaveRetentionSettingsAsync(HistoryRetentionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AudioRetentionDays < 0 || settings.TranscriptRetentionDays < 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Retention days cannot be negative.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key, value) VALUES('history_retention', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = await LoadRetentionSettingsAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var terminal = new[] { RecordingState.Completed, RecordingState.Cancelled, RecordingState.Failed }.Select(Stable).ToArray();
        var audioCutoff = settings.AudioRetentionDays == 0 ? null : now.AddDays(-settings.AudioRetentionDays).ToString(DateFormat, CultureInfo.InvariantCulture);
        var transcriptCutoff = settings.TranscriptRetentionDays == 0 ? null : now.AddDays(-settings.TranscriptRetentionDays).ToString(DateFormat, CultureInfo.InvariantCulture);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, created_at, audio_file_path FROM history WHERE state IN ($s0,$s1,$s2)";
        AddStates(select, terminal);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var deleteIds = new List<string>();
        var redactIds = new List<string>();
        var clearAudioIds = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var createdAt = reader.GetString(1);
            var transcriptExpired = transcriptCutoff is not null && string.CompareOrdinal(createdAt, transcriptCutoff) < 0;
            var audioExpired = !settings.SaveAudio || audioCutoff is not null && string.CompareOrdinal(createdAt, audioCutoff) < 0;
            if (transcriptExpired)
            {
                if (reader.IsDBNull(2) || DeleteAudio(reader.GetString(2)))
                    deleteIds.Add(id);
                else
                    redactIds.Add(id);
            }
            else if (audioExpired && !reader.IsDBNull(2) && DeleteAudio(reader.GetString(2)))
            {
                clearAudioIds.Add(id);
            }
        }
        await reader.DisposeAsync();
        foreach (var id in clearAudioIds)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE history SET audio_file_path = NULL WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var id in redactIds)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE history SET raw_transcript = NULL, cleaned_transcript = NULL, target_application = NULL, target_executable = NULL, domain = NULL, output_category = NULL, style_json = NULL, asr_model_name = NULL, cleanup_model_name = NULL WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var id in deleteIds)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM history WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task SaveAsync(HistoryEntry entry, bool update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = update ? $"UPDATE history SET {UpdateAssignments} WHERE id = $id" :
            $"INSERT INTO history ({Columns}) VALUES ({Values})";
        AddEntry(command, entry);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (update && affected == 0) throw new KeyNotFoundException($"History entry {entry.Id} was not found.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<HistoryEntry>> ReadManyAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadEntry(reader));
        return result;
    }

    private static HistoryEntry ReadEntry(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), ParseDate(r, 1)!.Value, ParseDate(r, 2), ParseDate(r, 3), ParseDuration(r, 4),
        GetString(r, 5), GetString(r, 6), GetString(r, 7), GetString(r, 8), GetString(r, 9), GetString(r, 10),
        ParseEnum<OutputContextCategory>(r, 11), DeserializeStyle(r, 12), GetString(r, 13), GetString(r, 14),
        ParseDuration(r, 15), ParseDuration(r, 16), ParseDuration(r, 17), ParseDuration(r, 18),
        ParseEnum<TextInsertionMethod>(r, 19), Enum.Parse<RecordingState>(r.GetString(20)),
        Enum.Parse<DictationErrorCode>(r.GetString(21)), r.GetInt32(22));

    private static void AddEntry(SqliteCommand c, HistoryEntry e)
    {
        Add(c, "$id", e.Id.ToString("D")); Add(c, "$created_at", Format(e.CreatedAt));
        Add(c, "$recording_started_at", Format(e.RecordingStartedAt)); Add(c, "$recording_ended_at", Format(e.RecordingEndedAt));
        Add(c, "$duration_ticks", e.Duration?.Ticks); Add(c, "$raw_transcript", e.RawTranscript); Add(c, "$cleaned_transcript", e.CleanedTranscript);
        Add(c, "$audio_file_path", e.AudioFilePath); Add(c, "$target_application", e.TargetApplication); Add(c, "$target_executable", e.TargetExecutable);
        Add(c, "$domain", e.Domain); Add(c, "$output_category", Stable(e.OutputCategory));
        Add(c, "$style_json", e.Style is null ? null : JsonSerializer.Serialize(e.Style, JsonOptions)); Add(c, "$asr_model_name", e.AsrModelName);
        Add(c, "$cleanup_model_name", e.CleanupModelName); Add(c, "$asr_duration_ticks", e.AsrDuration?.Ticks);
        Add(c, "$cleanup_duration_ticks", e.CleanupDuration?.Ticks); Add(c, "$insertion_duration_ticks", e.InsertionDuration?.Ticks);
        Add(c, "$total_duration_ticks", e.TotalDuration?.Ticks); Add(c, "$insertion_method", Stable(e.InsertionMethod));
        Add(c, "$state", Stable(e.State)); Add(c, "$error_code", Stable(e.ErrorCode)); Add(c, "$retry_count", e.RetryCount);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static void AddStates(SqliteCommand command, IReadOnlyList<string> states) { for (var i = 0; i < states.Count; i++) command.Parameters.AddWithValue($"$s{i}", states[i]); }
    private static string Stable<T>(T value) where T : struct, Enum => Enum.GetName(value)!;
    private static string? Stable<T>(T? value) where T : struct, Enum => value is null ? null : Stable(value.Value);
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
    private static string? Format(DateTimeOffset? value) => value is null ? null : Format(value.Value);
    private static DateTimeOffset? ParseDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.ParseExact(r.GetString(i), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static TimeSpan? ParseDuration(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : TimeSpan.FromTicks(r.GetInt64(i));
    private static string? GetString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static T? ParseEnum<T>(SqliteDataReader r, int i) where T : struct, Enum => r.IsDBNull(i) ? null : Enum.Parse<T>(r.GetString(i));
    private static TranscriptStyle? DeserializeStyle(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : JsonSerializer.Deserialize<TranscriptStyle>(r.GetString(i), JsonOptions);
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private bool DeleteAudio(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var relativePath = Path.GetRelativePath(_recordingsRoot, fullPath);
            if (relativePath == "." || Path.IsPathRooted(relativePath) || relativePath == ".." ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(fullPath), ".wav", StringComparison.OrdinalIgnoreCase))
                return false;

            using var root = CreateFile(_recordingsRoot, FileReadAttributes,
                FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, BackupSemantics | OpenReparsePoint, 0);
            if (root.IsInvalid || IsReparsePoint(root))
                return false;

            using var file = CreateFile(fullPath, DeleteAccess | FileReadAttributes,
                FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, OpenReparsePoint, 0);
            if (file.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                return error is ErrorFileNotFound or ErrorPathNotFound;
            }
            if (IsReparsePoint(file))
                return false;

            var rootPath = GetFinalPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var filePath = GetFinalPath(file);
            if (!filePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(file, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>());
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(SafeFileHandle file)
    {
        if (!GetFileInformationByHandleEx(file, 9, out var tagInfo, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new IOException("The recording attributes could not be read.");
        return (tagInfo.FileAttributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string GetFinalPath(SafeFileHandle file)
    {
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
            throw new IOException("The recording path could not be resolved.");
        return path.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, nint securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder filePath, uint filePathSize, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int fileInformationClass, out FileAttributeTagInfo fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int fileInformationClass, ref FileDispositionInfo fileInformation, uint bufferSize);

    private const string Columns = "id, created_at, recording_started_at, recording_ended_at, duration_ticks, raw_transcript, cleaned_transcript, audio_file_path, target_application, target_executable, domain, output_category, style_json, asr_model_name, cleanup_model_name, asr_duration_ticks, cleanup_duration_ticks, insertion_duration_ticks, total_duration_ticks, insertion_method, state, error_code, retry_count";
    private const string Values = "$id, $created_at, $recording_started_at, $recording_ended_at, $duration_ticks, $raw_transcript, $cleaned_transcript, $audio_file_path, $target_application, $target_executable, $domain, $output_category, $style_json, $asr_model_name, $cleanup_model_name, $asr_duration_ticks, $cleanup_duration_ticks, $insertion_duration_ticks, $total_duration_ticks, $insertion_method, $state, $error_code, $retry_count";
    private const string UpdateAssignments = "created_at=$created_at, recording_started_at=$recording_started_at, recording_ended_at=$recording_ended_at, duration_ticks=$duration_ticks, raw_transcript=$raw_transcript, cleaned_transcript=$cleaned_transcript, audio_file_path=$audio_file_path, target_application=$target_application, target_executable=$target_executable, domain=$domain, output_category=$output_category, style_json=$style_json, asr_model_name=$asr_model_name, cleanup_model_name=$cleanup_model_name, asr_duration_ticks=$asr_duration_ticks, cleanup_duration_ticks=$cleanup_duration_ticks, insertion_duration_ticks=$insertion_duration_ticks, total_duration_ticks=$total_duration_ticks, insertion_method=$insertion_method, state=$state, error_code=$error_code, retry_count=$retry_count";
}
