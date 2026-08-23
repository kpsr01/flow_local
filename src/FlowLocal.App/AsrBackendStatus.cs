namespace FlowLocal.App;

public enum AsrBackendState
{
    NotInstalled,
    Initializing,
    Downloading,
    Loading,
    Ready,
    Failed
}

public sealed record AsrBackendStatus(
    AsrBackendState State,
    string? ModelId = null,
    string? Provider = null,
    string? FailureMessage = null);
