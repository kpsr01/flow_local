using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class HandsFreeStateMachineTests
{
    [Fact]
    public void ConvertToHandsFree_FromPushToTalkSwitchesModeAndState()
    {
        var machine = new RecordingStateMachine();

        machine.Start(RecordingMode.PushToTalk);
        machine.BeginListening();
        machine.ConvertToHandsFree();

        Assert.Equal(RecordingState.ListeningHandsFree, machine.State);
        Assert.Equal(RecordingMode.HandsFree, machine.Mode);
    }

    [Fact]
    public void ConvertToHandsFree_AfterConversionContinuesThroughNormalFlow()
    {
        var machine = new RecordingStateMachine();

        machine.Start(RecordingMode.PushToTalk);
        machine.BeginListening();
        machine.ConvertToHandsFree();
        machine.TransitionTo(RecordingState.Stopping);
        machine.TransitionTo(RecordingState.Transcribing);
        machine.TransitionTo(RecordingState.Cleaning);
        machine.TransitionTo(RecordingState.Inserting);
        machine.TransitionTo(RecordingState.Completed);

        Assert.Equal(RecordingState.Completed, machine.State);
    }

    [Fact]
    public void ConvertToHandsFree_FromIdleRejected()
    {
        var machine = new RecordingStateMachine();

        Assert.Throws<InvalidOperationException>(machine.ConvertToHandsFree);
        Assert.Equal(RecordingState.Idle, machine.State);
    }

    [Fact]
    public void ConvertToHandsFree_DuringStartingRejected()
    {
        var machine = new RecordingStateMachine();

        machine.Start(RecordingMode.PushToTalk);

        Assert.Throws<InvalidOperationException>(machine.ConvertToHandsFree);
        Assert.Equal(RecordingState.Starting, machine.State);
        Assert.Equal(RecordingMode.PushToTalk, machine.Mode);
    }
}
