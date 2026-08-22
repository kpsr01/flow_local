namespace FlowLocal.App;

public sealed class AudioLevelEventArgs(float level) : EventArgs
{
    public float Level { get; } = level;
}
