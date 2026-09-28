// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

namespace MeisterDev.ProPR.Infrastructure.Tests.Forgejo;

/// <summary>
///     How a Forgejo reply body is rendered. A reply is a whole review with a body and no comments, so the
///     body carries the AI-generated marker once.
/// </summary>
public sealed class ForgejoReviewThreadReplyPublisherTests
{
    /// <summary>What the renderer puts after a tag opener so the provider stops reading it as markup.</summary>
    private const string ZeroWidthSpace = "\u200B";

    [Fact]
    public void FormatReplyText_NeutralizesMarkupWithoutManglingQuotedCode()
    {
        const string input =
            "Use \"--no-verify\" only after removing <script>alert('xss')</script> and <img src=x onerror=alert(1)>.";

        var reply = ForgejoReviewThreadReplyPublisher.FormatReplyText(input, TestPostedCommentComposer.Distinctive);

        Assert.Equal(
            "Use \"--no-verify\" only after removing <" + ZeroWidthSpace + "script>alert('xss')<" + ZeroWidthSpace
            + "/script> and <" + ZeroWidthSpace + "img src=x onerror=alert(1)>.\n\n"
            + TestPostedCommentComposer.DistinctiveMarker,
            reply);
    }

    [Fact]
    public void FormatReplyText_EndsWithTheMarker()
    {
        var reply = ForgejoReviewThreadReplyPublisher.FormatReplyText(
            "Fixed in the latest push.",
            TestPostedCommentComposer.Distinctive);

        Assert.Equal("Fixed in the latest push.\n\n" + TestPostedCommentComposer.DistinctiveMarker, reply);
    }

    [Fact]
    public void FormatReplyText_OnAQuotedReply_KeepsTheQuoteAndMarksTheWholeBodyOnce()
    {
        const string quoted = "> @propr why does this sort ascending?\n\nIt takes three.";

        var reply = ForgejoReviewThreadReplyPublisher.FormatReplyText(quoted, TestPostedCommentComposer.Distinctive);

        Assert.Equal(quoted + "\n\n" + TestPostedCommentComposer.DistinctiveMarker, reply);
        TestPostedCommentComposer.AssertMarkedOnce(reply, TestPostedCommentComposer.DistinctiveMarker);
    }
}
