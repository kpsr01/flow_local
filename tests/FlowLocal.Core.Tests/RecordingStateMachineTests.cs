using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class RecordingStateMachineTests
{
    [Theory]
    [InlineData(RecordingMode.PushToTalk, RecordingState.ListeningPushToTalk)]
    [InlineData(RecordingMode.HandsFree, RecordingState.ListeningHandsFree)]
    public void RecordingFlow_ReachesCompleted(RecordingMode mode, RecordingState listeningState)
    {
        var machine = new RecordingStateMachine();

        machine.Start(mode);
        Assert.Equal(RecordingState.Starting, machine.State);
        Assert.Equal(mode, machine.Mode);

        machine.BeginListening();
        Assert.Equal(listeningState, machine.State);

        machine.TransitionTo(RecordingState.Stopping);
        machine.TransitionTo(RecordingState.Transcribing);
        machine.TransitionTo(RecordingState.Cleaning);
        machine.TransitionTo(RecordingState.Inserting);
        machine.TransitionTo(RecordingState.Completed);

        Assert.Equal(RecordingState.Completed, machine.State);
        Assert.Equal(mode, machine.Mode);
    }

    [Fact]
    public void Start_RejectsDuplicateSession()
    {
        var machine = new RecordingStateMachine();
        machine.Start(RecordingMode.PushToTalk);

        Assert.Throws<InvalidOperationException>(() => machine.Start(RecordingMode.HandsFree));
        Assert.Equal(RecordingState.Starting, machine.State);
        Assert.Equal(RecordingMode.PushToTalk, machine.Mode);
    }

    [Fact]
    public void TransitionTo_RejectsInvalidTransition()
    {
        var machine = new RecordingStateMachine();

        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(RecordingState.Transcribing));
        Assert.Equal(RecordingState.Idle, machine.State);
        Assert.Null(machine.Mode);
    }

    [Fact]
    public void Cancel_CancelsSessionAndPreservesMode()
    {
        var machine = new RecordingStateMachine();
        var token = machine.Start(RecordingMode.HandsFree);
        machine.BeginListening();

        machine.Cancel();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(RecordingState.Cancelled, machine.State);
        Assert.Equal(RecordingMode.HandsFree, machine.Mode);
    }

    [Fact]
    public void Reset_IsAllowedOnlyFromTerminalState()
    {
        var machine = new RecordingStateMachine();
        machine.Start(RecordingMode.PushToTalk);

        Assert.Throws<InvalidOperationException>(machine.Reset);
        Assert.Equal(RecordingState.Starting, machine.State);
        Assert.Equal(RecordingMode.PushToTalk, machine.Mode);

        machine.Cancel();
        machine.Reset();

        Assert.Equal(RecordingState.Idle, machine.State);
        Assert.Null(machine.Mode);
    }
}
