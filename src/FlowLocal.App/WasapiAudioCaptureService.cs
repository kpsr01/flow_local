using System.Threading.Channels;
using FlowLocal.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FlowLocal.App;

public sealed record MicrophoneDeviceInfo(string Id, string Name, bool IsDefault);

public sealed class WasapiAudioCaptureService : IAudioCaptureService, IDisposable
{
    private static readonly WaveFormat CaptureFormat = new(16000, 16, 1);
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private Channel<byte[]>? _audio;
    private Task? _delivery;
    private TaskCompletionSource? _stopped;
    private bool _disposed;

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
    public event EventHandler<string>? FellBackToDefaultDevice;

    /// <summary>When true (default) every session captures the current Windows default recording endpoint.</summary>
    public bool FollowDefaultDevice { get; set; } = true;

    /// <summary>Pinned recording endpoint id used when <see cref="FollowDefaultDevice"/> is false.</summary>
    public string? PreferredDeviceId { get; set; }

    public static IReadOnlyList<MicrophoneDeviceInfo> ListCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; }
        catch (Exception) { }

        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new MicrophoneDeviceInfo(device.ID, device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.Ordinal)))
            .ToArray();
    }

    public Task StartAsync(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onAudio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onAudio);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_delivery is not null)
            {
                throw new InvalidOperationException("Audio capture is already running.");
            }

            var audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            var device = ResolveDevice(out var fellBackToDefault);
            var capture = device is null
                ? new WasapiCapture { WaveFormat = CaptureFormat }
                : new WasapiCapture(device) { WaveFormat = CaptureFormat };

            _audio = audio;
            _delivery = DeliverAsync(audio, onAudio, cancellationToken);
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _capture = capture;
            _device = device;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            try
            {
                capture.StartRecording();
            }
            catch
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                device?.Dispose();
                audio.Writer.TryComplete();
                _capture = null;
                _device = null;
                _audio = null;
                _delivery = null;
                _stopped = null;
                throw;
            }

            if (fellBackToDefault)
            {
                Task.Run(() => FellBackToDefaultDevice?.Invoke(this,
                    "The pinned microphone was unavailable; recording from the Windows default input device."));
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? stopped;
        Task delivery;
        lock (_gate)
        {
            if (_delivery is null)
            {
                return;
            }

            stopped = _stopped?.Task;
            delivery = _delivery;
            _capture?.StopRecording();
        }

        try
        {
            await Task.WhenAll(stopped ?? Task.CompletedTask, delivery).ConfigureAwait(false);
        }
        finally
        {
            if (delivery.IsCompleted && (stopped?.IsCompleted ?? true))
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_delivery, delivery))
                    {
                        _delivery = null;
                        _stopped = null;
                    }
                }
            }
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    private MMDevice? ResolveDevice(out bool fellBackToDefault)
    {
        fellBackToDefault = false;
        if (FollowDefaultDevice || string.IsNullOrWhiteSpace(PreferredDeviceId))
        {
            return null;
        }

        using var enumerator = new MMDeviceEnumerator();
        try
        {
            var device = enumerator.GetDevice(PreferredDeviceId!);
            if (!device.State.HasFlag(DeviceState.Active)) throw new InvalidOperationException("The pinned microphone is not active.");
            return device;
        }
        catch (Exception)
        {
            fellBackToDefault = true;
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var chunk = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        LevelChanged?.Invoke(this, new AudioLevelEventArgs(CalculateLevel(chunk)));

        var audio = _audio;
        if (audio is null)
        {
            return;
        }

        try
        {
            if (!audio.Writer.TryWrite(chunk))
            {
                audio.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    private static async Task DeliverAsync(
        Channel<byte[]> audio,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var chunk in audio.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await callback(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            audio.Writer.TryComplete(failure);
        }
    }
    private static float CalculateLevel(ReadOnlySpan<byte> audio)
    {
        var peak = 0;
        for (var i = 0; i + 1 < audio.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(audio[i..(i + 2)])));
        }

        return peak / 32768f;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        TaskCompletionSource? stopped;
        Channel<byte[]>? audio;
        MMDevice? device;
        lock (_gate)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            device = _device;
            _device = null;
            audio = _audio;
            _audio = null;
            stopped = _stopped;
        }

        device?.Dispose();
        audio?.Writer.TryComplete();
        if (e.Exception is null)
        {
            stopped?.TrySetResult();
        }
        else
        {
            stopped?.TrySetException(e.Exception);
        }
    }

    public void Dispose()
    {
        WasapiCapture? capture;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            capture = _capture;
        }

        capture?.StopRecording();
        GC.SuppressFinalize(this);
    }
}
