using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>Reads an explicit, fresh integration signal; it never guesses from arbitrary window titles.</summary>
internal static class CodingContextDetector
{
    internal const string TitlePrefix = "FlowLocal/1|";
    internal const int SignalLifetimeSeconds = 300;

    internal static CodingTarget? Detect(ActiveTarget target) =>
        Detect(target.ExecutableName, target.IsTerminal, target.WindowTitle, DateTimeOffset.UtcNow);

    internal static CodingTarget? Detect(string executable, bool terminal, string title, DateTimeOffset now)
    {
        var app = ApplicationNameCatalog.Normalize(executable);
        var category = ClassificationRules.Applications.GetValueOrDefault(app.ExecutableName);
        if (!terminal && category is not (OutputContextCategory.CodeEditor or OutputContextCategory.Terminal))
            return null;

        var unknown = new CodingTarget(app.DisplayName, null, null, "none", "No live model signal");
        if (!title.StartsWith(TitlePrefix, StringComparison.Ordinal) || title.Length > 256)
            return unknown;

        var fields = title.Split('|');
        if (fields.Length != 5 || fields[1] != "claude-code" ||
            !long.TryParse(fields[4], out var timestamp))
            return unknown with { UnknownReason = "Invalid model signal" };

        var age = now.ToUnixTimeSeconds() - timestamp;
        if (age < 0 || age > SignalLifetimeSeconds)
            return unknown with { UnknownReason = "Stale model signal" };
        if (!SafeId(fields[2]) || !SafeId(fields[3]))
            return unknown with { UnknownReason = "Invalid model signal" };

        var model = fields[2] == "unknown" ? null : fields[2];
        var reasoning = fields[3] == "unknown" ? null : fields[3];
        return new CodingTarget($"{app.DisplayName} / Claude Code", model, reasoning,
            "claude-code-statusline-v1",
            model is null ? "Model unavailable" : reasoning is null ? "Reasoning unavailable" : null);
    }

    private static bool SafeId(string value) => value.Length is > 0 and <= 80 &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
}
