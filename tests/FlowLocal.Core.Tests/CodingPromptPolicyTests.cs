using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class CodingPromptPolicyTests
{
    [Fact]
    public void CodingContext_ReadsFreshClaudeSignalFromSupportedTerminal()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var target = CodingContextDetector.Detect("WindowsTerminal.exe", true,
            $"FlowLocal/1|claude-code|claude-opus-4-7|medium|{now.ToUnixTimeSeconds()}", now);

        Assert.NotNull(target);
        Assert.Equal("claude-opus-4-7", target!.Model);
        Assert.Equal("medium", target.Reasoning);
        Assert.True(target.IsKnown);
    }

    [Fact]
    public void CodingContext_UsesExplicitUnknownStateForMissingOrStaleSignal()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var target = CodingContextDetector.Detect("code.exe", false, "project.ts - Visual Studio Code", now);
        var stale = CodingContextDetector.Detect("pwsh.exe", true,
            $"FlowLocal/1|claude-code|claude-opus-4-7|high|{now.ToUnixTimeSeconds() - 600}", now);

        Assert.NotNull(target);
        Assert.False(target!.IsKnown);
        Assert.NotNull(stale);
        Assert.False(stale!.IsKnown);
        Assert.Equal("Stale model signal", stale.UnknownReason);
        Assert.Null(CodingContextDetector.Detect("chrome.exe", false, "FlowLocal/1|claude-code|x|low|1700000000", now));
    }

    [Fact]
    public void BasicCodingRequest_PromptPreservesGoalConstraintAndNoSolutionInstruction()
    {
        var transcript = new RawTranscript("uh fix the login issue in the auth service and don't change the public api");
        var prompt = Build(transcript, "claude-opus-4-6", "medium");

        Assert.Contains(transcript.Text, prompt);
        Assert.Contains("goal and explicit constraints", prompt);
        Assert.Contains("Do not solve, diagnose, plan, expand, interpret", prompt);
        Assert.DoesNotContain("OAuth", prompt);
    }

    [Fact]
    public void CodingCleanupValidatorRejectsInventedSubstantiveWords()
    {
        var raw = new RawTranscript("figure out why this request fires twice in the profile component");

        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new CleanTranscriptResult("figure out why this request fires twice in the profile component.")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new CleanTranscriptResult("Investigate the profile component and verify the endpoint behavior.")));
    }

    [Fact]
    public void CodingCleanupValidatorAllowsOnlyFillerRemovalAndTerminalPunctuation()
    {
        var raw = new RawTranscript("uh fix the login issue in src/auth/session.ts");

        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new CleanTranscriptResult("fix the login issue in src/auth/session.ts.")));
    }

    [Fact]
    public void CodingCleanupValidatorRejectsDeletedConstraintsAndChangedTechnicalTokens()
    {
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(
            new RawTranscript("do not delete src/auth.ts"),
            new CleanTranscriptResult("delete src/auth.ts")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(
            new RawTranscript("use --no-cache"),
            new CleanTranscriptResult("use --nocache")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(
            new RawTranscript("use --no-cache now"),
            new CleanTranscriptResult("use --no-cache. now")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(
            new RawTranscript("git status"),
            new CleanTranscriptResult("Git. status")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(
            new RawTranscript("git status"),
            new CleanTranscriptResult("Git status")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(
            new RawTranscript("run echo um"),
            new CleanTranscriptResult("run echo")));
    }


    [Fact]
    public void TechnicalIdentifiersAndRequestedDirectionsRemainInTransformationInput()
    {
        var transcript = new RawTranscript("in src/auth/session.ts getUserById is returning undefined after the refresh; replace this polling loop with server sent events but keep the existing fallback");
        var prompt = Build(transcript, "claude-opus-4-6", "high");

        Assert.Contains("src/auth/session.ts", prompt);
        Assert.Contains("getUserById", prompt);
        Assert.Contains("server sent events", prompt);
        Assert.Contains("existing fallback", prompt);
        Assert.Contains("Do not produce code unless it was dictated", prompt);
    }

    [Fact]
    public void PoliciesChangeOrganizationWithoutChangingTheTranscript()
    {
        var transcript = new RawTranscript("figure out why this request fires twice in the profile component");
        var low = Build(transcript, "claude-opus-4-7", "low");
        var high = Build(transcript, "claude-opus-4-7", "high");

        Assert.Contains(transcript.Text, low);
        Assert.Contains(transcript.Text, high);
        Assert.Contains("concise checklist", low);
        Assert.DoesNotContain("concise checklist", high);
        Assert.Contains("Keep the requested scope focused", low);
        Assert.Contains("Keep the requested scope focused", high);
    }

    [Fact]
    public void CachedDetectionAndPolicyLookupStayCheap()
    {
        var now = DateTimeOffset.UtcNow;
        var title = $"FlowLocal/1|claude-code|claude-opus-4-7|high|{now.ToUnixTimeSeconds()}";
        _ = PromptPolicyRegistry.Default.Get(CodingContextDetector.Detect("pwsh.exe", true, title, now)!);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            var target = CodingContextDetector.Detect("pwsh.exe", true, title, now)!;
            _ = PromptPolicyRegistry.Default.Get(target);
        }
        stopwatch.Stop();
        Console.WriteLine($"10k coding detection+policy lookups: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }
    [Fact]
    public void UnknownModelOrReasoning_UsesGenericCodingPolicy()
    {
        var registry = PromptPolicyRegistry.Default;
        var unknownModel = registry.Get(new CodingTarget("Windows Terminal / Claude Code", "future-model", "medium", "test"));
        var unknownReasoning = registry.Get(new CodingTarget("Windows Terminal / Claude Code", "claude-opus-4-7", "extended", "test"));

        Assert.Equal("built-in generic coding cleanup", unknownModel.SourceReference);
        Assert.Equal("built-in generic coding cleanup", unknownReasoning.SourceReference);
    }

    private static string Build(RawTranscript transcript, string model, string reasoning)
    {
        var target = new CodingTarget("test", model, reasoning, "test");
        return DictationPromptAdapter.Build(transcript, target, PromptPolicyRegistry.Default.Get(target));
    }
}
