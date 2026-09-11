using System.IO;
using Microsoft.Extensions.Logging;

namespace FlowLocal.App;

internal sealed class PipelineMetricsLogger : ILogger<DictationController>
{
    private readonly string path;
    private readonly object gate = new();

    public PipelineMetricsLogger(string path) => this.path = path;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, null);
        if (!message.StartsWith("ASR metrics:", StringComparison.Ordinal) &&
            !message.StartsWith("Cleanup metrics:", StringComparison.Ordinal) &&
            !message.StartsWith("Dictation lifecycle", StringComparison.Ordinal)) return;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
