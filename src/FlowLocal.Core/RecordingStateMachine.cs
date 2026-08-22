namespace FlowLocal.Core;

public sealed class RecordingStateMachine
{
    private readonly object _gate = new();
    private CancellationTokenSource? _sessionCancellation;
    private RecordingMode? _mode;
    private RecordingState _state = RecordingState.Idle;

    public RecordingState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public RecordingMode? Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    public CancellationToken Start(RecordingMode mode, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureTransition(RecordingState.Starting);
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _mode = mode;
            _state = RecordingState.Starting;
            return _sessionCancellation.Token;
        }
    }

    public void BeginListening()
    {
        lock (_gate)
        {
            TransitionToCore(_mode switch
            {
                RecordingMode.PushToTalk => RecordingState.ListeningPushToTalk,
                RecordingMode.HandsFree => RecordingState.ListeningHandsFree,
                _ => throw new InvalidOperationException("No recording session is active.")
            });
        }
    }

    public void ConvertToHandsFree()
    {
        lock (_gate)
        {
            if (_state != RecordingState.ListeningPushToTalk)
            {
                throw new InvalidOperationException(
                    $"Hands-free conversion requires state {RecordingState.ListeningPushToTalk}, not {_state}.");
            }

            _mode = RecordingMode.HandsFree;
            TransitionToCore(RecordingState.ListeningHandsFree);
        }
    }

    public void TransitionTo(RecordingState next)
    {
        lock (_gate)
        {
            TransitionToCore(next);
        }
    }

    private void TransitionToCore(RecordingState next)
    {
        EnsureTransition(next);
        _state = next;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state is RecordingState.Idle or RecordingState.Completed or RecordingState.Cancelled or RecordingState.Failed)
            {
                throw new InvalidOperationException($"Cannot cancel a session in state {_state}.");
            }

            _sessionCancellation?.Cancel();
            _state = RecordingState.Cancelled;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            EnsureTransition(RecordingState.Idle);
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _mode = null;
            _state = RecordingState.Idle;
        }
    }

    private void EnsureTransition(RecordingState next)
    {
        var valid = (_state, next) switch
        {
            (RecordingState.Idle, RecordingState.Starting) => true,
            (RecordingState.Starting, RecordingState.ListeningPushToTalk or RecordingState.ListeningHandsFree) => true,
            (RecordingState.ListeningPushToTalk, RecordingState.ListeningHandsFree) => true,
            (RecordingState.ListeningPushToTalk or RecordingState.ListeningHandsFree, RecordingState.Stopping) => true,
            (RecordingState.Stopping, RecordingState.Transcribing) => true,
            (RecordingState.Transcribing, RecordingState.Cleaning) => true,
            (RecordingState.Cleaning, RecordingState.Inserting) => true,
            (RecordingState.Inserting, RecordingState.Completed) => true,
            (RecordingState.Completed or RecordingState.Cancelled or RecordingState.Failed, RecordingState.Idle) => true,
            (_, RecordingState.Cancelled or RecordingState.Failed) when _state is not RecordingState.Idle => true,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException($"Invalid recording transition: {_state} -> {next}.");
        }
    }
}
