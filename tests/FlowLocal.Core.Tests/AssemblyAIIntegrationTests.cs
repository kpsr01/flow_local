using System.Net;
using System.Net.Http;
using System.Text;
using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class AssemblyAIIntegrationTests
{
    private static readonly RawTranscript Raw = new("how do we fix the authentication bug in login dot t s", OutputContextCategory.CodeEditor);
    private static readonly TranscriptStyle Style = TranscriptStyleResolver.Resolve(OutputContextCategory.CodeEditor);

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"How do we fix\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"content\":null}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"\"}}]}")]
    [InlineData("not json")]
    public async Task UnusableGatewayResponse_PreservesEntireRawDictation(string response)
    {
        using var cleaner = Cleaner((_, _) => Task.FromResult(Json(response)));
        var result = await DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, default);
        Assert.True(result.UsedFallback);
        Assert.Equal(Raw.Text, result.Text);
    }

    [Fact]
    public async Task GatewayTimeoutFallsBack_ButUserCancellationDoesNotInsertFallback()
    {
        using var cleaner = Cleaner(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        }, TimeSpan.FromMilliseconds(30));
        var result = await cleaner.CleanAsync(Raw, Style, default);
        Assert.True(result.UsedFallback);
        Assert.Equal(Raw.Text, result.Text);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleaner.CleanAsync(Raw, Style, cancelled.Token));
    }

    [Fact]
    public async Task GatewayHttpErrorDoesNotDisableDictationOrRetryForAnotherTimeout()
    {
        var requests = 0;
        using var cleaner = Cleaner((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        var result = await DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, default);
        Assert.Equal(Raw.Text, result.Text);
        Assert.True(result.UsedFallback);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SyncAuthenticationErrorIsAnError_NotAnEmptyTranscriptOrLeakedResponse()
    {
        using var service = new AssemblyAIAsrService("test-key", new HttpClient(new Handler((request, _) =>
            Task.FromResult(request.Method == HttpMethod.Get ? Json("{}") : new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("private response body")
            }))));
        await service.InitializeAsync(default);
        await service.StartSessionAsync(new AsrSessionOptions(Guid.NewGuid()), default);
        await service.PushAudioAsync(new byte[3200], default);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.CompleteSessionAsync(default));
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain("private response body", exception.Message);
        Assert.DoesNotContain("test-key", exception.Message);
    }

    [Fact]
    public async Task SyncRejectsOutOfRangeAudioWithoutSubmittingTruncatedClips()
    {
        var submissions = 0;
        using var service = new AssemblyAIAsrService("test-key", new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) submissions++;
            return Task.FromResult(Json("{}"));
        })));
        await service.StartSessionAsync(new AsrSessionOptions(Guid.NewGuid()), default);
        await service.PushAudioAsync(new byte[2558], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSessionAsync(default));
        await service.StartSessionAsync(new AsrSessionOptions(Guid.NewGuid()), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PushAudioAsync(new byte[120 * 32000 + 2], default));
        await service.CancelSessionAsync(default);
        Assert.Equal(0, submissions);
    }

    [Fact]
    public async Task BrowserTitleFallbackCannotOverrideAddressBarOrWebsiteOptOut()
    {
        var target = new ActiveTarget(42, 123, "chrome.exe", "Inbox - Gmail - Google Chrome", DateTimeOffset.UnixEpoch);
        var classifier = new OutputStyleClassifier();
        var absent = new ApplicationContextDetector((_, _, _) => Task.FromResult<string?>(null));
        var gmail = await absent.DetectAsync(target, true, default);
        Assert.Equal(OutputContextCategory.Email, classifier.Classify(gmail, new()).Category);
        var optedOut = await absent.DetectAsync(target, false, default);
        Assert.Equal(OutputContextCategory.General, classifier.Classify(optedOut, new(WebsiteDetectionEnabled: false)).Category);
        var addressBar = new ApplicationContextDetector((_, _, _) => Task.FromResult<string?>("acme.slack.com"));
        var slack = await addressBar.DetectAsync(target, true, default);
        Assert.Equal(OutputContextCategory.WorkMessaging, classifier.Classify(slack, new()).Category);
    }

    [Fact]
    public async Task StalledBrowserProbeFallsBackWithoutStallingRecording()
    {
        var never = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new ApplicationContextDetector((_, _, _) => never.Task);
        var target = new ActiveTarget(42, 123, "chrome", "Inbox - Gmail", DateTimeOffset.UnixEpoch);
        var context = await detector.DetectAsync(target, true, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("mail.google.com", context.Domain);
        never.SetResult("late.example");
        Assert.Equal("mail.google.com", context.Domain);
    }

    [Fact]
    public void KeytermsRespectBudgetDeduplicateAndPrioritizeRememberedSpelling()
    {
        var words = new[] { " Priya ", "priya", "login.ts", "bad\nterm", new string('x', 101) }
            .Concat(Enumerable.Range(0, 30).Select(i => $"{i:D2}" + new string('a', 98)));
        var result = AssemblyAIPrompts.Keyterms(words);
        Assert.Equal(new[] { "Priya", "login.ts" }, result.Take(2));
        Assert.Equal(22, result.Count);
        Assert.True(result.Sum(term => term.Length) <= 2048);
        Assert.DoesNotContain("bad\nterm", result);
    }

    [Fact]
    public async Task TerminalMultilineTextIsCopiedIntactButNeverPasted()
    {
        var inserted = false;
        string? copied = null;
        var service = new ClipboardTextInsertionService(
            (_, _) => Task.FromResult(true),
            (_, _) => { inserted = true; return InsertionAttempt.Inserted(); },
            (_, _, _, _) => { inserted = true; return Task.FromResult(InsertionAttempt.Inserted()); },
            (_, _) => { inserted = true; return InsertionAttempt.Inserted(); },
            (text, _) => { copied = text; return Task.FromResult(InsertionAttempt.Inserted()); });
        var target = new ActiveTarget(42, 123, "pwsh", "Terminal", DateTimeOffset.UnixEpoch,
            CurrentIntegrityRid: 0x2000, TargetIntegrityRid: 0x2000, IsInjectionSafe: true, IsTerminal: true, IsPasswordField: false);
        const string commands = "echo review\r\nwhoami";
        var result = await service.InsertAsync(target, commands, default);
        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.ClipboardOnly, result.Method);
        Assert.False(inserted);
        Assert.Equal(commands, copied);
    }

    private static AssemblyAITranscriptCleaner Cleaner(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null) =>
        new("test-key", null, new HttpClient(new Handler(send)), timeout ?? TimeSpan.FromSeconds(1));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
