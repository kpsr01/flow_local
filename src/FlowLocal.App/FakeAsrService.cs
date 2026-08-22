using FlowLocal.Core;

namespace FlowLocal.App;

public sealed class FakeAsrService : IAsrService
{
    private bool _active;
    private bool _hasAudio;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StartSessionAsync(AsrSessionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _active = true;
        _hasAudio = false;
        return Task.CompletedTask;
    }

    public Task PushAudioAsync(ReadOnlyMemory<byte> pcmAudio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active)
            throw new InvalidOperationException("No ASR session is active.");

        _hasAudio |= !pcmAudio.IsEmpty;
        return Task.CompletedTask;
    }

    public Task<AsrResult> CompleteSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active)
            throw new InvalidOperationException("No ASR session is active.");

        _active = false;
        var result = new AsrResult(_hasAudio ? "This is a local dictation test." : "No speech was captured.");
        _hasAudio = false;
        return Task.FromResult(result);
    }

    public Task CancelSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _active = false;
        _hasAudio = false;
        return Task.CompletedTask;
    }
}
