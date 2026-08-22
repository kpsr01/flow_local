using System.IO;
using System.Text;
using FlowLocal.Core;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace FlowLocal.App;

public sealed class S1MiniTranscriptCleaner : ITranscriptCleaner, ICleanupBackend, IDisposable
{
    private const string ModelPathVariable = "FLOWLOCAL_CLEANUP_MODEL_PATH";
    private const string LegacyModelPathVariable = "FLOWLOCAL_S1_MODEL_PATH";
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly SemaphoreSlim inferenceLock = new(1, 1);
    private LLamaWeights? weights;
    private ModelParams? modelParameters;

    public string BackendId => "lfm25-cleanup-llamasharp";
    public string DisplayName => "LFM2.5-350M (local GGUF)";

    /// <summary>Configured GGUF path from FLOWLOCAL_CLEANUP_MODEL_PATH (or the legacy
    /// FLOWLOCAL_S1_MODEL_PATH), when set.</summary>
    public static string? ConfiguredModelPath =>
        ResolveModelPath();

    public bool IsLoaded => weights is not null;

    /// <summary>Where inference actually runs after the last load attempt (GPU offload or CPU fallback).</summary>
    public string ExecutionTarget { get; private set; } = "not loaded";

    public async Task<BackendAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return new BackendAvailability(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BackendAvailability(false, exception.Message);
        }
    }

    public async Task<CleanTranscriptResult> CleanAsync(
        RawTranscript transcript,
        TranscriptStyle style,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(style);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executor = new StatelessExecutor(weights!, modelParameters!);
            var output = new StringBuilder();
            var inference = new InferenceParams
            {
                MaxTokens = Math.Clamp(transcript.Text.Length, 64, 1024),
                SamplingPipeline = new GreedySamplingPipeline(),
                AntiPrompts = ["<|im_end|>", "<|endoftext|>"]
            };

            await foreach (var text in executor.InferAsync(
                DictationPromptAdapter.Build(transcript, style), inference, cancellationToken).ConfigureAwait(false))
            {
                output.Append(text);
            }

            return new CleanTranscriptResult(output.ToString().Trim());
        }
        finally
        {
            inferenceLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (weights is not null)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (weights is not null)
            {
                return;
            }

            var modelPath = ResolveModelPath();
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new InvalidOperationException(
                    $"Set {ModelPathVariable} (or legacy {LegacyModelPathVariable}) to the local GGUF cleanup model path.");
            }

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("The configured GGUF cleanup model was not found.", modelPath);
            }

            // CPU is the stable default on every machine. Set FLOWLOCAL_S1_GPU=1 to
            // experiment with full GPU offload (falls back to CPU automatically).
            var threads = Math.Max(4, Environment.ProcessorCount);
            var gpuRequested = string.Equals(
                Environment.GetEnvironmentVariable("FLOWLOCAL_S1_GPU"), "1", StringComparison.OrdinalIgnoreCase);
            int[] offloadPlan = gpuRequested ? [99, 0] : [0];
            foreach (var gpuLayers in offloadPlan)
            {
                modelParameters = new ModelParams(modelPath)
                {
                    ContextSize = 4096,
                    GpuLayerCount = gpuLayers,
                    Threads = threads,
                };
                try
                {
                    weights = await Task.Run(
                        () => LLamaWeights.LoadFromFile(modelParameters), cancellationToken).ConfigureAwait(false);
                    ExecutionTarget = gpuLayers > 0 ? $"GPU ({gpuLayers} layers, {threads} threads)" : $"CPU ({threads} threads)";
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (gpuLayers > 0)
                {
                    weights?.Dispose();
                    weights = null;
                    modelParameters = null;
                }
            }

            if (weights is null)
            {
                throw new InvalidOperationException("The GGUF cleanup model could not be loaded on GPU or CPU.");
            }
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private static string? ResolveModelPath()
    {
        var configured =
            Environment.GetEnvironmentVariable(ModelPathVariable) is { Length: > 0 } primary ? primary
            : Environment.GetEnvironmentVariable(LegacyModelPathVariable) is { Length: > 0 } legacy ? legacy
            : null;
        if (configured is not null) return configured;

        // Installer drops GGUF models here; no environment variable required.
        var modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal", "Models");
        if (!Directory.Exists(modelsDir)) return null;
        return Directory.GetFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        weights?.Dispose();
        initializationLock.Dispose();
        inferenceLock.Dispose();
    }
}
