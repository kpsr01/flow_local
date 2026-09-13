using System.IO;
using System.Text.Json;
using FlowLocal.Core;

namespace FlowLocal.App;

internal sealed record PromptingPolicy(
    string TargetModel,
    string ModelFamily,
    string ReasoningMode,
    string SourceReference,
    string SourceDate,
    IReadOnlyList<string> Rules);

internal sealed class PromptPolicyRegistry
{
    private sealed record Guide(
        string Model,
        string ModelFamily,
        string SourceReference,
        string SourceDate,
        Dictionary<string, Policy> Policies);

    private sealed record Policy(string[] ReasoningModes, string[] Rules);

    private static readonly Lazy<IReadOnlyDictionary<string, Guide>> Cached = new(Load);
    internal static PromptPolicyRegistry Default { get; } = new();

    internal PromptingPolicy Get(CodingTarget target)
    {
        if (!target.IsKnown || !Cached.Value.TryGetValue(target.Model!, out var guide))
            return Generic(target);

        var mode = target.Reasoning!;
        if (!guide.Policies.TryGetValue(mode, out var policy))
            guide.Policies.TryGetValue("default", out policy);
        if (policy is null || !policy.ReasoningModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            return Generic(target);

        return new PromptingPolicy(guide.Model, guide.ModelFamily, mode,
            guide.SourceReference, guide.SourceDate, policy.Rules);
    }

    internal static PromptingPolicy Generic(CodingTarget target) => new(
        target.Model ?? "unknown", "unknown", target.Reasoning ?? "unknown", "built-in generic coding cleanup", "",
        [
            "Keep the request focused on the stated goal and constraints.",
            "Preserve the user's requested delegation level; do not invent a plan or solution.",
            "Use concise sections only when they clarify the dictated request."
        ]);

    private static IReadOnlyDictionary<string, Guide> Load()
    {
        var root = FindGuideRoot();
        var guides = new Dictionary<string, Guide>(StringComparer.OrdinalIgnoreCase);
        if (root is null) return guides;
        foreach (var path in Directory.EnumerateFiles(root, "policy.json", SearchOption.AllDirectories))
        {
            try
            {
                var guide = JsonSerializer.Deserialize<Guide>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (guide is not null && !string.IsNullOrWhiteSpace(guide.Model))
                    guides[guide.Model] = guide;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return guides;
    }

    private static string? FindGuideRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "prompting-guides");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
