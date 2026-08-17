// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="MentionDetector" />.</summary>
public sealed class MentionDetectorTests
{
    private static readonly Guid ReviewerGuid = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    [Fact]
    public void IsMentioned_WithAdoGuidFormat_ReturnsTrue()
    {
        var content = $"@<{ReviewerGuid}> What do you think about this PR?";
        Assert.True(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithUpperCaseGuid_ReturnsTrue()
    {
        var content = $"@<{ReviewerGuid.ToString().ToUpperInvariant()}> Is this correct?";
        Assert.True(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithLowerCaseGuid_ReturnsTrue()
    {
        var content = $"@<{ReviewerGuid.ToString().ToLowerInvariant()}> Is this correct?";
        Assert.True(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithDifferentGuid_ReturnsFalse()
    {
        var content = $"@<{Guid.NewGuid()}> Can you review?";
        Assert.False(MentionDetector.IsMentioned(content, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_WithEmptyContent_ReturnsFalse()
    {
        Assert.False(MentionDetector.IsMentioned(string.Empty, ReviewerGuid));
    }

    [Fact]
    public void IsMentioned_ContentWithoutMention_ReturnsFalse()
    {
        Assert.False(MentionDetector.IsMentioned("This is a regular comment with no mentions.", ReviewerGuid));
    }

    /// <summary>
    ///     A quoted mention is a repetition of an earlier message, not a question. Where a provider offers no
    ///     thread to reply into, an answer opens with a quote of what it answers, so reading the quote as a
    ///     question would have every answer produce another one.
    /// </summary>
    [Fact]
    public void IsMentioned_MentionOnlyInsideAQuote_ReturnsFalse()
    {
        var quotedAnswer = $"> @<{ReviewerGuid}> What is this supposed to do?\n\nIt sorts ascending.";

        Assert.False(MentionDetector.IsMentioned(quotedAnswer, ReviewerGuid));
    }

    /// <summary>Quotes nest, and a quote of a quote is still a quote.</summary>
    [Fact]
    public void IsMentioned_MentionInsideANestedQuote_ReturnsFalse()
    {
        var quotedTwice = $"> > @<{ReviewerGuid}> What is this?\n> \n> It is nothing.\n\nUnderstood.";

        Assert.True(MentionDetector.IsMentioned(quotedTwice, ReviewerGuid) is false);
    }

    [Fact]
    public void IsMentioned_MentionAskedUnderAQuote_ReturnsTrue()
    {
        var followUp = $"> It sorts ascending.\n\n@<{ReviewerGuid}> then why is it labelled latest?";

        Assert.True(MentionDetector.IsMentioned(followUp, ReviewerGuid));
    }

    /// <summary>Markdown allows a blockquote to be indented, up to three spaces before the marker.</summary>
    [Fact]
    public void IsMentioned_MentionInsideAnIndentedQuote_ReturnsFalse()
    {
        Assert.False(MentionDetector.IsMentioned($"  > @<{ReviewerGuid}> What is this?", ReviewerGuid));
    }

    /// <summary>A greater-than sign inside a line is not a quote, and must not hide a real question.</summary>
    [Fact]
    public void IsMentioned_GreaterThanSignMidLine_StillReturnsTrue()
    {
        Assert.True(MentionDetector.IsMentioned($"@<{ReviewerGuid}> is a > b here?", ReviewerGuid));
    }
}
