// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

public sealed class ReviewCommentTests
{
    [Fact]
    public void AsPullRequestLevel_DropsTheAnchorAndTakesTheNewMessage()
    {
        var comment = BuildFullyPopulatedComment();

        var promoted = comment.AsPullRequestLevel("src/Legacy.cs:L244: The delete path races.");

        Assert.Null(promoted.FilePath);
        Assert.Null(promoted.LineNumber);
        Assert.Equal("src/Legacy.cs:L244: The delete path races.", promoted.Message);
        Assert.Equal(comment.Severity, promoted.Severity);
    }

    [Fact]
    public void AsPullRequestLevel_CarriesEveryPieceOfProvenance()
    {
        var comment = BuildFullyPopulatedComment();

        var promoted = comment.AsPullRequestLevel("rewritten");

        Assert.Equal(ReviewCommentScopeRelation.OutsideChange, promoted.ScopeRelation);
        Assert.Equal("MultiPassUnion", promoted.OriginPassKind);
        Assert.Equal(3, promoted.OriginPassIndex);
        Assert.Equal("security", promoted.OriginPassLens);
        Assert.True(promoted.OriginPassShadow);
        Assert.Equal(comment.SourceReadGrounding, promoted.SourceReadGrounding);
        Assert.Equal("gpt-x", promoted.OriginModelId);
        Assert.Equal("reviewer-default", promoted.OriginLogicalModelName);
        Assert.Equal("DeleteAsync", promoted.OriginSymbolName);
        Assert.Equal("method", promoted.OriginSymbolKind);
    }

    [Fact]
    public void AsPullRequestLevel_CopiesEveryPropertyExceptTheAnchorAndMessage()
    {
        // A guard against the next property. Every rewrite site went through four values and silently lost the
        // rest, so this asserts over the type's own surface rather than over a list somebody has to remember to
        // extend: a new provenance property that AsPullRequestLevel forgets fails here.
        var comment = BuildFullyPopulatedComment();

        var promoted = comment.AsPullRequestLevel("rewritten");

        var anchorAndMessage = new[]
        {
            nameof(ReviewComment.FilePath),
            nameof(ReviewComment.LineNumber),
            nameof(ReviewComment.Message),
        };

        var carried = typeof(ReviewComment)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !anchorAndMessage.Contains(property.Name, StringComparer.Ordinal))
            .ToList();

        Assert.NotEmpty(carried);
        foreach (var property in carried)
        {
            var original = property.GetValue(comment);
            Assert.NotNull(original);
            Assert.Equal(original, property.GetValue(promoted));
        }
    }

    private static ReviewComment BuildFullyPopulatedComment()
    {
        return new ReviewComment("src/Legacy.cs", 244, CommentSeverity.Warning, "The delete path races.")
        {
            OriginPassKind = "MultiPassUnion",
            OriginPassIndex = 3,
            OriginPassLens = "security",
            OriginPassShadow = true,
            ScopeRelation = ReviewCommentScopeRelation.OutsideChange,
            SourceReadGrounding = ReviewCommentReadGrounding.Covered,
            OriginModelId = "gpt-x",
            OriginLogicalModelName = "reviewer-default",
            OriginSymbolName = "DeleteAsync",
            OriginSymbolKind = "method",
        };
    }
}
