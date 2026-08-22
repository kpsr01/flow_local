namespace FlowLocal.App;

internal enum InsertionDisposition
{
    Inserted,
    Unsupported,
    Failed,
    UnknownSideEffect
}

internal readonly record struct InsertionAttempt(InsertionDisposition Disposition, string? Error = null)
{
    internal static InsertionAttempt Unsupported(string? error = null) =>
        new(InsertionDisposition.Unsupported, error);

    internal static InsertionAttempt Inserted() =>
        new(InsertionDisposition.Inserted);

    internal static InsertionAttempt Failed(string error) =>
        new(InsertionDisposition.Failed, error);

    internal static InsertionAttempt UnknownSideEffect(string error) =>
        new(InsertionDisposition.UnknownSideEffect, error);
}
