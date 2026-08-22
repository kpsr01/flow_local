using FlowLocal.Core;

namespace FlowLocal.Core.Tests;

public sealed class CleanupValidationTests
{
    [Fact]
    public void AcceptsFaithfulCleanup()
    {
        var raw = new RawTranscript("um send the report tomorrow please");
        var cleaned = new CleanTranscriptResult("Send the report tomorrow, please.");

        Assert.True(CleanupResultValidator.TryValidate(raw, cleaned, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void RejectsEmptyOutput()
    {
        AssertRejected("hello world", "   ", "empty");
    }

    [Fact]
    public void RejectsAnyModelControlTokenLeakage()
    {
        AssertRejected("hello world", "Hello world.<|startoftranscript|>", "control tokens");
    }

    [Fact]
    public void RejectsImplausiblyExpandedOutput()
    {
        AssertRejected(
            "Send the report tomorrow",
            "Send the report tomorrow with a detailed introduction, executive summary, risk assessment, implementation plan, stakeholder analysis, budget forecast, legal review, appendix, references, and follow-up recommendations for every department.",
            "expanded or unrelated");
    }

    [Fact]
    public void RejectsRepeatedWordRunawayOutput()
    {
        AssertRejected(
            "Send the report tomorrow",
            "Send the report tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow tomorrow.",
            "expanded or unrelated");
    }

    [Fact]
    public void RejectsImplausiblyUnrelatedOutput()
    {
        AssertRejected(
            "Send the report tomorrow please",
            "Penguins migrate across distant frozen oceans each winter.",
            "expanded or unrelated");
    }

    [Theory]
    [InlineData("Please email Priya at priya@example.com", "Please email Priya at priya@example.com.")]
    [InlineData("The total is 25 dollars and 50 cents", "The total is $25.50.")]
    [InlineData("Schedule it Thursday at five thirty p m", "Schedule it Thursday at 5:30 PM.")]
    [InlineData("There are three tasks first update second email third schedule", "There are 3 tasks: 1. Update. 2. Email. 3. Schedule.")]
    public void AcceptsFaithfulNormalizationOfStructuredTranscript(string raw, string cleaned)
    {
        Assert.True(CleanupResultValidator.TryValidate(
            new RawTranscript(raw),
            new CleanTranscriptResult(cleaned),
            out var reason));
        Assert.Null(reason);
    }

    private static void AssertRejected(string raw, string cleaned, string expectedReason)
    {
        Assert.False(CleanupResultValidator.TryValidate(
            new RawTranscript(raw),
            new CleanTranscriptResult(cleaned),
            out var reason));
        Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
    }
}
