// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

public sealed class ReviewSynthesisExecutorMaterializeTests
{
    private static CandidateReviewFinding Finding(string findingId, string message)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
            CommentSeverity.Error,
            message,
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12);
    }

    private static FinalGateDecision Publish(string findingId, string? publicationNote)
    {
        return new FinalGateDecision(
            findingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            "default_publish_rules",
            [],
            null,
            null,
            publicationNote);
    }

    private static FinalGateDecision Drop(string findingId)
    {
        return new FinalGateDecision(
            findingId,
            FinalGateDecision.DropDisposition,
            [ReviewFindingGateReasonCodes.ErrorFindingRereadContradicted],
            "reread_contradicted_rules",
            [],
            null,
            null);
    }

    [Fact]
    public void MaterializePublishedComments_AppendsPublicationNoteToTheComment()
    {
        var finding = Finding("finding-a", "Potential null dereference.");

        var comments = ReviewSynthesisExecutor.MaterializePublishedComments(
            [finding],
            [Publish("finding-a", "⚠️ Unverified — check this.")]);

        var comment = Assert.Single(comments);
        Assert.StartsWith("Potential null dereference.", comment.Message);
        Assert.Contains("⚠️ Unverified — check this.", comment.Message);
        Assert.Equal(CommentSeverity.Error, comment.Severity);
    }

    [Fact]
    public void MaterializePublishedComments_WithoutNote_LeavesTheMessageUnchanged()
    {
        var finding = Finding("finding-a", "Potential null dereference.");

        var comments = ReviewSynthesisExecutor.MaterializePublishedComments([finding], [Publish("finding-a", null)]);

        var comment = Assert.Single(comments);
        Assert.Equal("Potential null dereference.", comment.Message);
    }

    [Fact]
    public void MaterializePublishedComments_DroppedFinding_ProducesNoComment()
    {
        var finding = Finding("finding-a", "Cited line does not exist.");

        var comments = ReviewSynthesisExecutor.MaterializePublishedComments([finding], [Drop("finding-a")]);

        Assert.Empty(comments);
    }
}
