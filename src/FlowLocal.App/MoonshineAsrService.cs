using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>
/// Thin client for the FlowLocal.AsrWorker companion process. All Moonshine ONNX
/// inference runs inside the worker so stalls or native aborts never take the app down;
/// a wedged or crashed worker is killed and transparently respawned for the next session.
/// </summary>
public sealed class MoonshineAsrService : IAsrService, IDisposable, IAsyncDisposable
{
    public const string ModelName = "moonshine-streaming-medium";

    private static readonly TimeSpan InitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartAckTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CompleteTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _worker;
    private StreamWriter? _stdin;
    private Task? _reader;
    private TaskCompletionSource<bool>? _ackSource;
    private TaskCompletionSource<string?>? _finalSource;
    private volatile string? _pendingStreamError;
    private bool _initialized;
    private bool _disposed;

    public AsrBackendStatus Status { get; private set; } = new(AsrBackendState.NotInstalled);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SampleRate != 16_000 || options.BitsPerSample != 16 || options.Channels != 1)
        {
            throw new ArgumentException("Moonshine ASR requires 16000 Hz, 16-bit, mono PCM audio.", nameof(options));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetSessionState();
            await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
            await RequestAckAsync(new { cmd = "start" }, StartAckTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingStreamError is { } failure)
        {
            throw new InvalidOperationException($"Speech recognition failed: {failure}");
        }

        var payload = Convert.ToBase64String(pcmAudio.Span);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsWorkerAlive) throw new InvalidOperationException("The ASR worker is no longer running.");
            await SendAsync(new { cmd = "push", b64 = payload }).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingStreamError is { } earlyFailure)
            {
                throw new InvalidOperationException($"Speech recognition failed: {earlyFailure}");
            }

            if (!IsWorkerAlive) throw new InvalidOperationException("The ASR worker is no longer running.");

            var finalSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _finalSource = finalSource;
            await SendAsync(new { cmd = "complete" }).ConfigureAwait(false);
            string? text;
            try
            {
                text = await finalSource.Task.WaitAsync(CompleteTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("Speech recognition stalled and was abandoned.");
            }

            return new AsrResult(text ?? throw new InvalidOperationException("No speech was recognized."));
        }
        finally
        {
            _finalSource = null;
            _lifecycle.Release();
        }
    }

    public async Task CancelSessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetSessionState();
            if (IsWorkerAlive)
            {
                try
                {
                    await RequestAckAsync(new { cmd = "cancel" }, CancelTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Cancellation must never surface; the session is being discarded anyway.
                    KillWorker();
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ResetSessionState()
    {
        _pendingStreamError = null;
        _finalSource = null;
        _ackSource = null;
    }

    private bool IsWorkerAlive => _worker is { HasExited: false };

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (IsWorkerAlive && _initialized)
        {
            Status = new(AsrBackendState.Ready, ModelName);
            return;
        }

        // First-run model download/ONNX session warm-up inside the worker can occasionally
        // stall; a fresh process retries instead of hanging forever.
        for (var attempt = 1; ; attempt++)
        {
            if (!IsWorkerAlive)
            {
                StartWorker();
            }

            Status = new(AsrBackendState.Initializing, ModelName);
            try
            {
                await RequestAckAsync(new { cmd = "init" }, InitTimeout, cancellationToken).ConfigureAwait(false);
                _initialized = true;
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt == 1)
            {
                KillWorker();
            }
        }
    }

    private void StartWorker()
    {
        KillWorker();
        var workerPath = Path.Combine(AppContext.BaseDirectory, "FlowLocal.AsrWorker.exe");
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The FlowLocal ASR worker executable was not found.", workerPath);
        }

        var info = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        var process = Process.Start(info)
            ?? throw new InvalidOperationException("The FlowLocal ASR worker could not be started.");
        _worker = process;
        _stdin = process.StandardInput;
        _initialized = false;
        Status = new(AsrBackendState.Initializing, ModelName);

        _reader = ReaderLoopAsync(process);
        _ = process.StandardError.ReadToEndAsync();
    }

    private async Task ReaderLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                RouteEvent(line);
            }
        }
        catch (Exception)
        {
        }
        FailPendingWaits(process.HasExited ? $"ASR worker exited (code {process.ExitCode})." : "ASR worker output ended.");
    }

    private void RouteEvent(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            switch (root.TryGetProperty("evt", out var evt) ? evt.GetString() : null)
            {
                case "status":
                    Status = new(
                        root.TryGetProperty("state", out var stateEl) && Enum.TryParse<AsrBackendState>(stateEl.GetString(), out var parsed)
                            ? parsed
                            : AsrBackendState.Initializing,
                        ModelName,
                        Provider: root.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String
                            ? detailEl.GetString()
                            : null);
                    break;

                case "ok":
                    Interlocked.Exchange(ref _ackSource, null)?.TrySetResult(true);
                    break;

                case "final":
                    Interlocked.Exchange(ref _finalSource, null)?.TrySetResult(
                        root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null);
                    break;

                case "error":
                    var message = root.TryGetProperty("message", out var messageEl) ? messageEl.GetString() : "Unknown ASR error.";
                    if (Interlocked.Exchange(ref _finalSource, null) is { } pendingFinal)
                    {
                        pendingFinal.TrySetResult(null);
                        _pendingStreamError = message;
                    }
                    else
                    {
                        _pendingStreamError = message;
                    }
                    break;
            }
        }
    }

    private void FailPendingWaits(string reason)
    {
        Interlocked.Exchange(ref _ackSource, null)?.TrySetException(new InvalidOperationException(reason));
        Interlocked.Exchange(ref _finalSource, null)?.TrySetException(new InvalidOperationException(reason));
        _pendingStreamError ??= reason;
    }

    private async Task RequestAckAsync(object command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ackSource = ack;
        await SendAsync(command).ConfigureAwait(false);
        try
        {
            await ack.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The ASR worker did not respond to '{command}' in time.");
        }
    }

    private async Task SendAsync(object command)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("The ASR worker is not running.");
        await stdin.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
        await stdin.FlushAsync().ConfigureAwait(false);
    }

    private void KillWorker()
    {
        var worker = _worker;
        _worker = null;
        _stdin = null;
        if (worker is null) return;
        try
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        worker.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsWorkerAlive)
        {
            try
            {
                await SendAsync(new { cmd = "exit" }).ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        KillWorker();
        _lifecycle.Dispose();
    }
}
