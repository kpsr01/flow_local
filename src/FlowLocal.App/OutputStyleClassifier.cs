using FlowLocal.Core;
using System.IO;

namespace FlowLocal.App;

public sealed class OutputStyleClassifier : IOutputStyleClassifier
{
    public OutputClassification Classify(ApplicationContext context, OutputStyleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.StyleClassificationEnabled)
            return Result(
                settings.UniversalDefaultCategory,
                settings.UniversalDefaultStyle,
                ClassificationSource.General,
                "StyleClassificationDisabled",
                context.Detection);

        var domain = settings.WebsiteDetectionEnabled ? NormalizeHost(context.Domain) : null;
        if (domain is not null && TryDomainOverride(settings.DomainOverrides, domain, out var domainOverride, out var domainRule))
            return OverrideResult(domainOverride, ClassificationSource.DomainOverride, domainRule, context.Detection);

        var executable = NormalizeExecutable(context.ExecutableName);
        if (TryExecutableOverride(settings.ExecutableOverrides, executable, out var executableOverride, out var executableRule))
            return OverrideResult(executableOverride, ClassificationSource.ExecutableOverride, executableRule, context.Detection);

        if (domain is not null)
        {
            foreach (var rule in ClassificationRules.Domains)
                if (ClassificationRules.HostMatches(domain, rule.Domain))
                    return Result(rule.Category, null, ClassificationSource.KnownDomain, rule.Domain, context.Detection);
        }

        if (ClassificationRules.Applications.TryGetValue(executable, out var applicationCategory))
            return Result(applicationCategory, null, ClassificationSource.KnownApplication, executable, context.Detection);

        if (ClassificationRules.FromControlHint(context.ControlType) is { } controlCategory)
            return Result(controlCategory, null, ClassificationSource.ControlHint, context.ControlType!, context.Detection);

        if (context.IsBrowser)
            return Result(OutputContextCategory.General, null, ClassificationSource.GenericBrowser, "Browser", context.Detection);

        return Result(OutputContextCategory.General, null, ClassificationSource.General, "General", context.Detection);
    }

    private static OutputClassification Result(
        OutputContextCategory category,
        TranscriptStyle? explicitStyle,
        ClassificationSource source,
        string rule,
        ContextDetectionDiagnostic diagnostic) =>
        new(category, explicitStyle ?? TranscriptStyleResolver.Resolve(category), source, rule, diagnostic);

    private static OutputClassification OverrideResult(
        OutputStyleOverride value,
        ClassificationSource source,
        string rule,
        ContextDetectionDiagnostic diagnostic) =>
        Result(value.Category, value.Style, source, rule, diagnostic);

    private static bool TryDomainOverride(
        IReadOnlyDictionary<string, OutputStyleOverride>? overrides,
        string host,
        out OutputStyleOverride value,
        out string rule)
    {
        if (overrides is not null)
        {
            foreach (var candidate in overrides)
            {
                var normalized = NormalizeHost(candidate.Key);
                if (normalized is not null && ClassificationRules.HostMatches(host, normalized))
                {
                    value = candidate.Value;
                    rule = normalized;
                    return true;
                }
            }
        }

        value = null!;
        rule = "";
        return false;
    }

    private static bool TryExecutableOverride(
        IReadOnlyDictionary<string, OutputStyleOverride>? overrides,
        string executable,
        out OutputStyleOverride value,
        out string rule)
    {
        if (overrides is not null)
        {
            foreach (var candidate in overrides)
            {
                var normalized = NormalizeExecutable(candidate.Key);
                if (executable.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    rule = normalized;
                    return true;
                }
            }
        }

        value = null!;
        rule = "";
        return false;
    }

    private static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            candidate = uri.IdnHost;
        else
        {
            var delimiter = candidate.IndexOfAny(['/', ':']);
            if (delimiter >= 0)
                candidate = candidate[..delimiter];
        }

        candidate = candidate.Trim().TrimEnd('.').ToLowerInvariant();
        return candidate.Length == 0 ? null : candidate;
    }

    private static string NormalizeExecutable(string? value)
    {
        var executable = Path.GetFileName(value?.Trim() ?? "");
        return executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable[..^4].ToLowerInvariant()
            : executable.ToLowerInvariant();
    }
}
