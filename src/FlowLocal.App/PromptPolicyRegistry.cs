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
        Dictionary<string, Policy> Policies,
        string[]? Models = null);

    private sealed record Policy(string[] ReasoningModes, string[] Rules);

    private static readonly Lazy<IReadOnlyDictionary<string, Guide>> Cached = new(Load);
    internal static PromptPolicyRegistry Default { get; } = new();

    internal PromptingPolicy Get(CodingTarget target)
    {
        var model = target.Model;
        if (model?.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase) == true) model = model[..^4];
        if (!target.IsKnown || !Cached.Value.TryGetValue(model!, out var guide))
            return Generic(target);

        var mode = target.Reasoning ?? "unknown";
        if (!guide.Policies.TryGetValue(mode.ToLowerInvariant(), out var policy) ||
            !policy.ReasoningModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            guide.Policies.TryGetValue("default", out policy);
        if (policy is null) return Generic(target);

        return new PromptingPolicy(target.Model!, guide.ModelFamily, mode,
            guide.SourceReference, guide.SourceDate, policy.Rules);
    }

    internal static PromptingPolicy Generic(CodingTarget target) => new(
        target.Model ?? "unknown", "unknown", target.Reasoning ?? "unknown", "built-in generic coding cleanup", "",
        [
            "Keep the stated goal, context, constraints, and requested output clear and separate when needed.",
            "Use paragraphs for distinct topics and bullets for existing requirements; retain the original order.",
            "Preserve all dictated plan steps and acceptance criteria; never supply missing ones.",
            "Keep questions and uncertainty intact. Do not answer, recommend, infer a solution, or increase delegation.",
            "Use plain, concise language without adding role prompts, tool instructions, examples, or reasoning requests."
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
                if (guide is not null && !string.IsNullOrWhiteSpace(guide.Model) &&
                    guide.Policies is not null && guide.Policies.Values.All(p => p is not null &&
                        p.Rules is { Length: > 0 } && p.Rules.All(r => !string.IsNullOrWhiteSpace(r)) &&
                        p.ReasoningModes is not null))
                {
                    guides[guide.Model] = guide;
                    foreach (var model in guide.Models ?? [])
                        if (!string.IsNullOrWhiteSpace(model)) guides[model] = guide;
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
