namespace FlowLocal.App;

public enum FoundryLocalState
{
    NotInstalled,
    Initializing,
    Downloading,
    Loading,
    Ready,
    Failed
}

public sealed record FoundryLocalStatus(
    FoundryLocalState State,
    string? ModelId = null,
    string? Provider = null,
    string? FailureMessage = null);
