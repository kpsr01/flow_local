using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using FlowLocal.Core;

namespace FlowLocal.App;

/// <summary>Matches fixed harness chrome from the captured terminal surface or an explicit harness signal.</summary>
internal static class CodingContextDetector
{
    internal const string TitlePrefix = "FlowLocal/1|";
    internal const int SignalLifetimeSeconds = 300;

    internal static CodingTarget? Detect(ActiveTarget target) =>
        Detect(target.ExecutableName, target.IsTerminal, target.WindowTitle, DateTimeOffset.UtcNow);

    internal static Task<CodingTarget?> DetectAsync(ActiveTarget target, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var title = Detect(target);
            if (target.IsTerminal)
                return title?.IsKnown == true ? title : DetectTerminalSurface(target, title, cancellationToken);
            return IsDesktopHarness(target.ExecutableName)
                ? DetectDesktopSurface(target, cancellationToken) ?? title
                : title;
        }, CancellationToken.None);

    internal static CodingTarget? DetectVisibleText(string executable, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var app = ApplicationNameCatalog.Normalize(executable);
        var environment = app.DisplayName;
        var adapters = Regex.Matches(text,
            @"FlowLocal (?<kind>OMP|Pi):\s*(?<model>[A-Za-z0-9._/:@+\[\]-]+)\s*/\s*(?<reasoning>[A-Za-z0-9._/:@+\[\]-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (adapters.Count > 0)
        {
            var adapter = adapters[^1];
            var model = adapter.Groups["model"].Value;
            var reasoning = adapter.Groups["reasoning"].Value;
            var harness = adapter.Groups["kind"].Value.Equals("OMP", StringComparison.OrdinalIgnoreCase)
                ? "Oh My Pi" : "Pi";
            var valid = SafeId(model) && SafeId(reasoning);
            return new CodingTarget(environment, valid && model != "unknown" ? model : null,
                valid && reasoning != "unknown" ? reasoning : null, "terminal-uia",
                !valid ? "Invalid model signal" : model == "unknown" ? "Model unavailable" : null, harness);
        }

        if (app.ExecutableName is "chatgpt" or "codex")
            return DetectOpenAiSurface(environment, text,
                app.ExecutableName == "codex" ? "Codex Desktop" : "ChatGPT Desktop");
        if (app.ExecutableName == "claude")
            return DetectClaudeSurface(environment, text, "Claude Desktop");

        var codex = Regex.Match(text,
            @"OpenAI Codex[\s\S]{0,600}?\bmodel:\s+(?<model>[A-Za-z0-9._/:@+\[\]-]+)\s+(?<reasoning>none|minimal|low|medium|high|xhigh)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (text.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase))
            return codex.Success && SafeId(codex.Groups["model"].Value)
                ? new CodingTarget(environment, codex.Groups["model"].Value,
                    codex.Groups["reasoning"].Value.ToLowerInvariant(), "terminal-uia", Harness: "Codex")
                : new CodingTarget(environment, null, null, "terminal-uia", "Model line unavailable", "Codex");

        var statusLine = Regex.Match(text,
            @"Claude Code:\s*(?<model>[A-Za-z0-9._/:@+\[\]-]+)\s*/\s*(?<reasoning>[A-Za-z0-9._/:@+\[\]-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (statusLine.Success)
        {
            var model = statusLine.Groups["model"].Value;
            var reasoning = statusLine.Groups["reasoning"].Value;
            var valid = SafeId(model) && SafeId(reasoning);
            return new CodingTarget(environment, valid && model != "unknown" ? model : null,
                valid && reasoning != "unknown" ? reasoning : null, "terminal-uia",
                !valid ? "Invalid model signal" : model == "unknown" ? "Model unavailable" : null, "Claude Code");
        }

        var claude = Regex.Match(text,
            @"Claude Code v[\s\S]{0,500}?\b(?<family>Opus|Sonnet|Haiku)\s+(?<version>\d+(?:\.\d+)*)[^\r\n]*?\bwith\s+(?<reasoning>low|medium|high|max)\s+effort\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (text.Contains("Claude Code v", StringComparison.OrdinalIgnoreCase))
            return claude.Success
                ? new CodingTarget(environment,
                    ClaudeModelId(claude.Groups["family"].Value, claude.Groups["version"].Value),
                    claude.Groups["reasoning"].Value.ToLowerInvariant(), "terminal-uia", Harness: "Claude Code")
                : new CodingTarget(environment, null, null, "terminal-uia", "Model line unavailable", "Claude Code");

        var ompMatches = Regex.Matches(text,
            @"(?m)^\s*(?:π\s*·\s*)?(?:\S+\s+\S+\s*·\s*)?◒\s+(?<model>[A-Za-z0-9][A-Za-z0-9._/:@+\[\] -]{0,100}?)\s+·\s*📁");
        if (ompMatches.Count > 0)
        {
            var model = NormalizeDisplayedModel(ompMatches[^1].Groups["model"].Value);
            return new CodingTarget(environment, model, null, "terminal-uia",
                model is null ? "Model line unavailable" : null, "Oh My Pi");
        }
        if (text.Contains("omp v", StringComparison.OrdinalIgnoreCase))
            return new CodingTarget(environment, null, null, "terminal-uia", "Model line unavailable", "Oh My Pi");

        if (Regex.IsMatch(text, @"(?m)^\s*pi v\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var matches = Regex.Matches(text,
                @"(?m)(?<model>[A-Za-z0-9][A-Za-z0-9._/:@+\[\]-]{2,})\s+•\s+(?:thinking\s+)?(?<reasoning>off|none|minimal|low|medium|high|max|xhigh)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (matches.Count == 0)
                return new CodingTarget(environment, null, null, "terminal-uia", "Model line unavailable", "Pi");
            var match = matches[^1];
            return new CodingTarget(environment, match.Groups["model"].Value,
                match.Groups["reasoning"].Value.ToLowerInvariant(), "terminal-uia", Harness: "Pi");
        }

        return null;
    }

    private static CodingTarget DetectOpenAiSurface(string environment, string text, string harness)
    {
        var match = Regex.Match(text,
            @"(?im)^\s*(?:Model(?:[ \t]+selector)?(?:,[ \t]*current[ \t]+model[ \t]+is)?[ \t]*:?[ \t]*)?(?<model>GPT-[A-Za-z0-9._-]+(?:[ \t]+(?!(?:none|minimal|light|low|medium|high|extra[ \t]+high|max|ultra|persistent)\b)[A-Za-z0-9._-]+){0,4})(?:[ \t]+(?<reasoning>none|minimal|light|low|medium|high|extra[ \t]+high|max|ultra|persistent))?",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return new CodingTarget(environment, null, null, "desktop-uia", "Model control unavailable", harness);
        var reasoning = match.Groups["reasoning"].Success
            ? match.Groups["reasoning"].Value
            : Regex.Match(text,
                @"(?im)^\s*(?:Selected\s+)?(?<reasoning>none|minimal|light|low|medium|high|extra\s+high|max|ultra|persistent)\s*$",
                RegexOptions.CultureInvariant).Groups["reasoning"].Value;
        reasoning = reasoning.ToLowerInvariant() switch
        {
            "light" => "low",
            "extra high" => "xhigh",
            "" => "",
            var value => value
        };
        return new CodingTarget(environment, NormalizeDisplayedModel(match.Groups["model"].Value),
            reasoning.Length == 0 ? null : reasoning, "desktop-uia", Harness: harness);
    }

    private static CodingTarget DetectClaudeSurface(string environment, string text, string harness)
    {
        var model = Regex.Match(text,
            @"(?im)^\s*(?:Model\s*:\s*)?(?:Claude\s+)?(?<family>Opus|Sonnet|Haiku)\s+(?<version>\d+(?:\.\d+)*)",
            RegexOptions.CultureInvariant);
        if (!model.Success)
            return new CodingTarget(environment, null, null, "desktop-uia", "Model control unavailable", harness);
        var effort = Regex.Match(text,
            @"(?im)^\s*(?:Selected\s+)?(?<reasoning>low|medium|high|max)(?:\s+effort)?\s*$",
            RegexOptions.CultureInvariant);
        return new CodingTarget(environment,
            ClaudeModelId(model.Groups["family"].Value, model.Groups["version"].Value),
            effort.Success ? effort.Groups["reasoning"].Value.ToLowerInvariant() : null,
            "desktop-uia", Harness: harness);
    }

    private static CodingTarget? DetectDesktopSurface(ActiveTarget target, CancellationToken cancellationToken)
    {
        try
        {
            RequestAccessibility(target.WindowHandle);
            cancellationToken.ThrowIfCancellationRequested();
            var root = AutomationElement.FromHandle(target.WindowHandle);
            var nodes = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)));
            var controls = new StringBuilder();
            for (var i = 0; i < nodes.Count && controls.Length < 24_000; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = nodes[i];
                var name = node.Current.Name;
                if (node.Current.IsOffscreen || string.IsNullOrWhiteSpace(name)) continue;
                var selected =
                    node.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle) &&
                    toggle is TogglePattern { Current.ToggleState: ToggleState.On } ||
                    node.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) &&
                    selection is SelectionItemPattern { Current.IsSelected: true };
                controls.AppendLine(selected ? $"Selected {name}" : name);
            }
            return DetectVisibleText(target.ExecutableName, controls.ToString());
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or
            NotSupportedException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDesktopHarness(string executable) =>
        ApplicationNameCatalog.Normalize(executable).ExecutableName is "chatgpt" or "codex" or "claude";


    private static void RequestAccessibility(nint windowHandle) =>
        NativeMethods.SendMessageTimeout(windowHandle, NativeMethods.WmGetObject, 0, NativeMethods.ObjIdClient,
            NativeMethods.SmtoAbortIfHung, 100, out _);

    private static string ClaudeModelId(string family, string version) =>
        $"claude-{family.ToLowerInvariant()}-{version.Replace('.', '-')}";

    private static string? NormalizeDisplayedModel(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", "-").ToLowerInvariant();
        return SafeId(normalized) ? normalized : null;
    }
    private static CodingTarget? DetectTerminalSurface(
        ActiveTarget target, CodingTarget? fallback, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = AutomationElement.FromHandle(target.WindowHandle);
            var nodes = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            for (var i = 0; i < nodes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodes[i].TryGetCurrentPattern(TextPattern.Pattern, out var pattern) ||
                    pattern is not TextPattern textPattern) continue;
                foreach (var range in textPattern.GetVisibleRanges())
                    if (DetectVisibleText(target.ExecutableName, range.GetText(24_000)) is { } detected)
                        return detected;
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or
            NotSupportedException or COMException or UnauthorizedAccessException)
        {
        }
        return fallback;
    }

    internal static CodingTarget? Detect(string executable, bool terminal, string title, DateTimeOffset now)
    {
        var app = ApplicationNameCatalog.Normalize(executable);
        var category = ClassificationRules.Applications.GetValueOrDefault(app.ExecutableName);
        if (!terminal && category is not (OutputContextCategory.CodeEditor or OutputContextCategory.Terminal))
            return null;

        var unknown = new CodingTarget(app.DisplayName, null, null, "none", "No live model signal");
        if (title.Length > 1024) return unknown with { UnknownReason = "Invalid model signal" };
        // Codex's native title updates on /model and clears on normal exit. The supplied launcher
        // selects app-name, session-id, model, reasoning, with no project/user text.
        var native = title.Split(" | ", StringSplitOptions.None);
        if (native.Length == 4 && native[0] == "codex")
        {
            var target = unknown with { Harness = "Codex", SignalSource = "codex-terminal-title" };
            var session = native[1].TrimEnd('.', '…');
            if (session.Length is not (29 or 31 or 32 or 36) || !session.All(c => char.IsAsciiHexDigit(c) || c == '-'))
                return target with { UnknownReason = "Invalid Codex session signal" };
            var model = native[2] switch { "Luna Reserve" => "gpt-reserve", "loading" => "unknown", _ => native[2] };
            if (model.EndsWith("...", StringComparison.Ordinal) || model.EndsWith('…'))
                model = ReadCodexModel(session, model.TrimEnd('.', '…'), now) ?? "unknown";
            if (!SafeId(model)) return target with { UnknownReason = "Model unavailable or truncated" };
            var reasoning = native[3] is "default" or "unknown" ? null : native[3];
            return target with
            {
                Model = model == "unknown" ? null : model,
                Reasoning = reasoning is not null && SafeId(reasoning) ? reasoning : null,
                UnknownReason = model == "unknown" ? "Full model ID unavailable; enable the Codex hook" : null
            };
        }
        if (!title.StartsWith(TitlePrefix, StringComparison.Ordinal)) return unknown;
        var fields = title.Split('|');
        if (fields.Length != 5 || fields[1] is not ("claude-code" or "codex" or "pi" or "omp") ||
            !long.TryParse(fields[4], out var timestamp))
            return unknown with { UnknownReason = "Invalid model signal" };

        unknown = unknown with { Harness = fields[1] switch { "codex" => "Codex", "claude-code" => "Claude Code", "pi" => "Pi", _ => "Oh My Pi" } };
        if (timestamp > now.ToUnixTimeSeconds() || timestamp < now.ToUnixTimeSeconds() - SignalLifetimeSeconds)
            return unknown with { UnknownReason = "Stale model signal" };
        if (!SafeId(fields[2]) || !SafeId(fields[3]))
            return unknown with { UnknownReason = "Invalid model signal" };

        return unknown with
        {
            Model = fields[2] == "unknown" ? null : fields[2],
            Reasoning = fields[3] == "unknown" ? null : fields[3],
            SignalSource = $"{fields[1]}-statusline-v1",
            UnknownReason = fields[2] == "unknown" ? "Model unavailable" : null
        };
    }

    private static string? ReadCodexModel(string session, string prefix, DateTimeOffset now)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal", "CodingSignals", session[..29] + ".json");
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4096) return null;
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            var id = root.GetProperty("sessionId").GetString();
            var model = root.GetProperty("model").GetString();
            var timestamp = root.GetProperty("timestamp").GetInt64();
            return id is not null && id.StartsWith(session, StringComparison.Ordinal) &&
                timestamp <= now.ToUnixTimeSeconds() && timestamp >= now.ToUnixTimeSeconds() - SignalLifetimeSeconds &&
                model is not null && SafeId(model) && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? model : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    internal static bool SafeId(string value) => value.Length is > 0 and <= 512 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '/' or ':' or '@' or '+' or '[' or ']');
}
