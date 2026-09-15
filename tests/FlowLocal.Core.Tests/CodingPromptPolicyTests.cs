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


    [Theory]
    [InlineData(
        "OpenAI Codex (v0.154.0)\r\nmodel:     gpt-5.6-sol medium   /model to change",
        "Codex", "gpt-5.6-sol", "medium")]
    [InlineData(
        "Claude Code v2.1.270\r\nOpus 5 (1M context) with low effort · API Usage Billing",
        "Claude Code", "claude-opus-5", "low")]
    [InlineData(
        "omp v18.2.0\r\nπ · ◒ GPT-5.6-Sol · 📁 ~/repo · ⑂ main",
        "Oh My Pi", "gpt-5.6-sol", null)]
    [InlineData(
        "pi v0.60.0\r\n~/repo\r\n?%/272k                     gpt-5.4 • high",
        "Pi", "gpt-5.4", "high")]
    [InlineData(
        "FlowLocal OMP: openai-codex/gpt-5.6-sol / xhigh",
        "Oh My Pi", "openai-codex/gpt-5.6-sol", "xhigh")]
    [InlineData(
        "FlowLocal Pi: anthropic/claude-opus-4-6 / max",
        "Pi", "anthropic/claude-opus-4-6", "max")]
    public void CodingContext_ReadsNormalHarnessTerminalSurface(
        string terminalText, string harness, string model, string? reasoning)
    {
        var target = CodingContextDetector.DetectVisibleText("WindowsTerminal.exe", terminalText);

        Assert.Equal(harness, target?.Harness);
        Assert.Equal(model, target?.Model);
        Assert.Equal(reasoning, target?.Reasoning);
        Assert.Equal("terminal-uia", target?.SignalSource);
    }

    [Fact]
    public void CodingContext_UsesLatestVisiblePiStatusAfterModelSwitch()
    {
        var target = CodingContextDetector.DetectVisibleText("WindowsTerminal.exe",
            "FlowLocal Pi: old-model / low\r\nFlowLocal Pi: new-model / high");

        Assert.Equal("new-model", target?.Model);
        Assert.Equal("high", target?.Reasoning);
    }

    [Theory]
    [InlineData(
        "ChatGPT.exe",
        "Chat\r\nSelected Work\r\nFull access\r\nGPT-5.4 Mini Medium None Minimal Light Medium High Extra High Max Ultra Persistent\r\nChoose project",
        "Codex Desktop", "gpt-5.4-mini", "medium")]
    [InlineData(
        "claude.exe",
        "Chat\r\nCowork\r\nSelected Code\r\nOpus 4.6\r\nHigh\r\nAccept edits",
        "Claude Desktop", "claude-opus-4-6", "high")]
    public void CodingContext_ReadsDesktopCodingControls(
        string executable, string controls, string harness, string model, string reasoning)
    {
        var target = CodingContextDetector.DetectVisibleText(executable, controls);

        Assert.Equal(harness, target?.Harness);
        Assert.Equal(model, target?.Model);
        Assert.Equal(reasoning, target?.Reasoning);
        Assert.Equal("desktop-uia", target?.SignalSource);
    }

    [Theory]
    [InlineData("ChatGPT.exe", "Selected Chat\r\nGPT-5.4 Mini\r\nMedium")]
    [InlineData("claude.exe", "Selected Chat\r\nOpus 4.6\r\nHigh")]
    public void CodingContext_DoesNotTreatDesktopChatAsCoding(string executable, string controls) =>
        Assert.Null(CodingContextDetector.DetectVisibleText(executable, controls));

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
        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw, new CleanTranscriptResult(raw.Text)));
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
    public void UnknownModelsUseGenericPolicyButMissingEffortRetainsKnownModelGuidance()
    {
        var registry = PromptPolicyRegistry.Default;
        var unknownModel = registry.Get(new CodingTarget("Windows Terminal / Claude Code", "future-model", "medium", "test"));
        var unknownReasoning = registry.Get(new CodingTarget("Windows Terminal / Claude Code", "claude-opus-4-7", "extended", "test"));

        Assert.Equal("built-in generic coding cleanup", unknownModel.SourceReference);
        Assert.Equal("Claude Opus", unknownReasoning.ModelFamily);
    }

    [Theory]
    [InlineData("codex", "vendor/Future_Model:latest", "Codex")]
    [InlineData("claude-code", "anthropic.claude-next@20990101", "Claude Code")]
    [InlineData("pi", "vendor/Future_Model:latest", "Pi")]
    [InlineData("omp", "vendor/Future_Model:latest", "Oh My Pi")]
    public void ModelDetectionDoesNotRequireAnAllowlistOrEffort(string signal, string model, string harness)
    {
        var now = DateTimeOffset.UtcNow;
        var target = CodingContextDetector.Detect("pwsh.exe", true,
            $"FlowLocal/1|{signal}|{model}|unknown|{now.ToUnixTimeSeconds()}", now)!;
        Assert.True(target.IsKnown);
        Assert.Equal(model, target.Model);
        Assert.Equal(harness, target.Harness);
        Assert.Null(target.Reasoning);
        Assert.Equal("unknown", PromptPolicyRegistry.Default.Get(target).ModelFamily);
    }

    [Fact]
    public void CodexNativeTitleTracksModelSwitchesWithoutGuessingFromProjectNames()
    {
        var now = DateTimeOffset.UtcNow;
        const string session = "12345678-1234-1234-1234-123456789abc";
        var first = CodingContextDetector.Detect("pwsh.exe", true, $"codex | {session} | gpt-5.3-codex | high", now)!;
        var second = CodingContextDetector.Detect("pwsh.exe", true, $"codex | {session} | future-model | default", now)!;
        Assert.Equal("Codex", first.Harness);
        Assert.Equal("gpt-5.3-codex", first.Model);
        Assert.Equal("OpenAI Codex", PromptPolicyRegistry.Default.Get(first).ModelFamily);
        Assert.Equal("future-model", second.Model);
        Assert.Null(second.Reasoning);
        Assert.False(CodingContextDetector.Detect("pwsh.exe", true, "my gpt-5.3-codex project", now)!.IsKnown);
    }

    [Theory]
    [InlineData("bad model", 0)]
    [InlineData("model", 1)]
    [InlineData("model", -301)]
    public void InvalidOrNoncurrentSignalsNeverSelectModelGuidance(string model, long offset)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(CodingContextDetector.Detect("pwsh.exe", true,
            $"FlowLocal/1|codex|{model}|high|{now.ToUnixTimeSeconds() + offset}", now)!.IsKnown);
    }

    [Fact]
    public void FormattingCanStructureExistingPlansButCannotAddSteps()
    {
        var raw = new RawTranscript("uh inspect the failure then fix the parser keep --no-cache unchanged");
        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new CleanTranscriptResult("1. inspect the failure.\n2. then fix the parser.\n3. keep --no-cache unchanged.")));
        Assert.False(CodingCleanupValidator.PreservesSubstantiveWords(raw,
            new CleanTranscriptResult("1. inspect the failure.\n2. then fix the parser.\n3. keep --no-cache unchanged.\n4. run tests.")));
    }

    [Fact]
    public void FormattingAllowsModelGuideHeadingsWithoutChangingTheRequest()
    {
        var raw = new RawTranscript(
            "fix the parser in src/parser.ts keep --no-cache unchanged do not change the public API return a short summary");
        var formatted = new CleanTranscriptResult(
            "Task:\nfix the parser in src/parser.ts\n\nConstraints:\nkeep --no-cache unchanged.\ndo not change the public API.\n\nOutput:\nreturn a short summary.");

        Assert.True(CodingCleanupValidator.PreservesSubstantiveWords(raw, formatted));
        var grammar = CodingCleanupValidator.CreateFormattingGrammar(raw);
        Assert.Contains("\"Task:\\n\"", grammar);
        Assert.Contains("\"Constraints:\\n\"", grammar);
        Assert.DoesNotContain("\"Task:\\n\"",
            CodingCleanupValidator.CreateFormattingGrammar(new RawTranscript("fix src/auth.ts")));
        Assert.Contains("\"Output:\\n\"", grammar);
    }

    [Fact]
    public void CodingPromptUsesSelectedModelGuideWithoutFewShotOverhead()
    {
        var target = new CodingTarget("Windows Terminal / Codex", "gpt-5.3-codex", "medium", "test");
        var prompt = DictationPromptAdapter.Build(new RawTranscript("fix src/auth.ts"),
            PromptPolicyRegistry.Default.Get(target));

        Assert.Contains("Prefer a direct task followed by requirement bullets", prompt);
        Assert.Contains("fix src/auth.ts", prompt);
        Assert.DoesNotContain("uh inspect src/auth.ts", prompt);
    }

    [Fact]
    public void ClaudeExtendedContextKeepsFullIdentityAndUsesItsBaseModelGuide()
    {
        var now = DateTimeOffset.UtcNow;
        var target = CodingContextDetector.Detect("pwsh.exe", true,
            $"FlowLocal/1|claude-code|claude-opus-5[1m]|low|{now.ToUnixTimeSeconds()}", now)!;
        Assert.Equal("claude-opus-5[1m]", target.Model);
        Assert.Equal("Claude Opus 5", PromptPolicyRegistry.Default.Get(target).ModelFamily);
    }

    [Fact]
    public void TruncatedCodexModelsRequireMatchingFreshSessionMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Guid.NewGuid().ToString("D");
        const string model = "vendor/Very_Long_Future_Model:2099.01-preview@cloud";
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowLocal", "CodingSignals");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, session[..29] + ".json");
        var title = $"codex | {session[..29]}... | {model[..29]}... | high";
        try
        {
            void Save(string id, long timestamp) => File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(new { sessionId = id, model, timestamp }));
            Save(session, now.ToUnixTimeSeconds());
            Assert.Equal(model, CodingContextDetector.Detect("pwsh.exe", true, title, now)!.Model);
            Save(Guid.NewGuid().ToString("D"), now.ToUnixTimeSeconds());
            Assert.False(CodingContextDetector.Detect("pwsh.exe", true, title, now)!.IsKnown);
            Save(session, now.ToUnixTimeSeconds() - 301);
            Assert.False(CodingContextDetector.Detect("pwsh.exe", true, title, now)!.IsKnown);
        }
        finally { File.Delete(path); }
    }

}
