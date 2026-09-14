using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>Checks GitHub Releases, downloads a newer installer, verifies it, and launches it.</summary>
public static class UpdateService
{
    public const string ManifestUrl = "https://github.com/kpsr01/flow_local/releases/latest/download/latest.json";

    // Infinite overall timeout: it would also bound streaming reads of the large installer download.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public sealed record CheckResult(UpdateManifest? Update, string? Error)
    {
        public static readonly CheckResult UpToDate = new(null, null);
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static async Task<CheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await Http.GetAsync(ManifestUrl, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var manifest = UpdateManifest.TryParse(json);
            if (manifest is null) return new CheckResult(null, "The update information is invalid.");
            return manifest.IsNewerThan(CurrentVersion)
                ? new CheckResult(manifest, null)
                : CheckResult.UpToDate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new CheckResult(null, $"Could not reach the update server: {exception.Message}");
        }
    }

    /// <summary>Downloads the installer to a temp file and verifies its required SHA-256.</summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(Path.GetTempPath(), $"FlowLocal-update-{manifest.Version}-setup.exe");
        try
        {
            using var response = await Http.GetAsync(
                manifest.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (!Convert.ToHexString(sha.Hash ?? []).Equals(
                        manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The downloaded update failed its integrity check.");
                }
            }
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Starts the installer after this process has had time to release its files.</summary>
    public static void Apply(string installerPath)
    {
        installerPath = Path.GetFullPath(installerPath);
        if (!File.Exists(installerPath)) throw new FileNotFoundException("The update installer was not found.", installerPath);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c timeout /t 2 /nobreak >nul & start \"\" \"" + installerPath +
                        "\" /SILENT /SUPPRESSMSGBOXES /SP- /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        if (process is null) throw new InvalidOperationException("The update installer could not be started.");
    }
}
