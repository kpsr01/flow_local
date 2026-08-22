using FlowLocal.App;
using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class CleanupFallbackTests
{
    private static readonly RawTranscript Raw = new("raw dictated text");
    private static readonly TranscriptStyle Style = new("General", "Neutral", "Prose");

    [Fact]
    public async Task SuccessfulCleanup_ReturnsCleanedTextForInsertion()
    {
        var cleaner = new SequenceCleaner(new CleanTranscriptResult("cleaned text"));

        var result = await DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, CancellationToken.None);

        Assert.Equal("cleaned text", result.Text);
        Assert.Equal(1, cleaner.Calls);
    }

    [Fact]
    public async Task CleanupFailure_RetriesOnce_ThenFallsBackToRawText()
    {
        var cleaner = new SequenceCleaner(
            new InvalidOperationException("first failure"),
            new InvalidOperationException("second failure"));

        var result = await DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, CancellationToken.None);

        Assert.Equal(Raw.Text, result.Text);
        Assert.Equal(2, cleaner.Calls);
    }

    [Fact]
    public async Task InvalidCleanup_RetriesOnce_ThenFallsBackToRawText()
    {
        var cleaner = new SequenceCleaner(new CleanTranscriptResult(""), new CleanTranscriptResult("  "));

        var result = await DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, CancellationToken.None);

        Assert.Equal(Raw.Text, result.Text);
        Assert.Equal(2, cleaner.Calls);
    }

    [Fact]
    public async Task ValidatorRejectedCleanup_RetriesOnce_ThenFallsBackToRawText()
    {
        var invalid = new CleanTranscriptResult("<|assistant|> invented output");
        var cleaner = new SequenceCleaner(invalid, invalid);

        var result = await DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, CancellationToken.None);

        Assert.Equal(Raw.Text, result.Text);
        Assert.Equal(2, cleaner.Calls);
    }

    [Fact]
    public async Task Cancellation_IsNotRetriedOrConvertedToFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cleaner = new SequenceCleaner(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DictationController.CleanWithFallbackAsync(cleaner, Raw, Style, cancellation.Token));

        Assert.Equal(1, cleaner.Calls);
    }

    [Fact]
    public async Task EmptyRawTranscript_FailsBeforeCleanup()
    {
        var cleaner = new SequenceCleaner(new CleanTranscriptResult("should not be used"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DictationController.CleanWithFallbackAsync(cleaner, new RawTranscript("  "), Style, CancellationToken.None));

        Assert.Equal(0, cleaner.Calls);
    }

    private sealed class SequenceCleaner(params object[] outcomes) : ITranscriptCleaner
    {
        public int Calls { get; private set; }

        public Task<CleanTranscriptResult> CleanAsync(
            RawTranscript transcript,
            TranscriptStyle style,
            CancellationToken cancellationToken)
        {
            var outcome = outcomes[Calls++];
            return outcome is Exception exception
                ? Task.FromException<CleanTranscriptResult>(exception)
                : Task.FromResult((CleanTranscriptResult)outcome);
        }
    }
}
