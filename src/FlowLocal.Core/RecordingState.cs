namespace FlowLocal.Core;

public enum RecordingState
{
    Idle,
    Starting,
    ListeningPushToTalk,
    ListeningHandsFree,
    Stopping,
    Transcribing,
    Cleaning,
    Inserting,
    Completed,
    Cancelled,
    Failed
}
