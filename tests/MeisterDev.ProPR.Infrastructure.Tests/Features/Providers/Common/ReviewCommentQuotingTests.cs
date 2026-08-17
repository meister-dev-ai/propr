// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

/// <summary>
///     The quoted reply a provider without a native thread reply answers with, in the shape those providers'
///     own quote reply produces.
/// </summary>
public sealed class ReviewCommentQuotingTests
{
    [Fact]
    public void TheQuestionIsQuotedAboveTheAnswer()
    {
        var reply = ReviewCommentQuoting.BuildQuotedReply(
            "@propr What is this supposed to do?",
            "It sorts ascending and then takes three.");

        Assert.Equal(
            "> @propr What is this supposed to do?\n\nIt sorts ascending and then takes three.",
            reply);
    }

    /// <summary>
    ///     An answer that is itself quoted later still reads as a conversation, which is what makes a
    ///     follow-up question answerable without any thread.
    /// </summary>
    [Fact]
    public void AQuotedAnswerCanItselfBeQuoted()
    {
        var first = ReviewCommentQuoting.BuildQuotedReply("What is this?", "This is nothing.");
        var second = ReviewCommentQuoting.BuildQuotedReply(first, "Then why is it here?");

        Assert.Equal(
            "> > What is this?\n> \n> This is nothing.\n\nThen why is it here?",
            second);
    }

    /// <summary>
    ///     A blank line inside the quote keeps its marker. Without it the block ends there and the rest of the
    ///     question renders as if the answer had said it.
    /// </summary>
    [Fact]
    public void ABlankLineInsideTheQuoteKeepsItsMarker()
    {
        var reply = ReviewCommentQuoting.BuildQuotedReply("First paragraph.\n\nSecond paragraph.", "Answered.");

        Assert.Equal("> First paragraph.\n> \n> Second paragraph.\n\nAnswered.", reply);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToQuoteLeavesTheAnswerAlone(string? quoted)
    {
        Assert.Equal("Answered.", ReviewCommentQuoting.BuildQuotedReply(quoted, "Answered."));
    }

    /// <summary>
    ///     A question long enough to bury the answer is quoted for recognition, not for reading.
    /// </summary>
    [Fact]
    public void ALongQuestionIsCutShortRatherThanBuryingTheAnswer()
    {
        var question = string.Join('\n', Enumerable.Range(1, 40).Select(line => $"line {line}"));

        var reply = ReviewCommentQuoting.BuildQuotedReply(question, "Answered.");

        Assert.Contains("> line 1\n", reply, StringComparison.Ordinal);
        Assert.Contains("> …\n", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("line 40", reply, StringComparison.Ordinal);
        Assert.EndsWith("\nAnswered.", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void CarriageReturnsDoNotSurviveIntoTheQuote()
    {
        var reply = ReviewCommentQuoting.BuildQuotedReply("First.\r\nSecond.", "Answered.");

        Assert.Equal("> First.\n> Second.\n\nAnswered.", reply);
    }
}
