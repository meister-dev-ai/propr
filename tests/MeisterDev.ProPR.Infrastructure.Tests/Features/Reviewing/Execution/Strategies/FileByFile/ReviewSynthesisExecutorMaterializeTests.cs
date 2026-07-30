// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

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
    public void MaterializePublishedComments_CarriesTheProducingModelOntoThePublishedComment()
    {
        // The published comment is what the collection path sees, so an attribution that stops at the gate is an
        // attribution that never reaches a metric.
        var finding = new CandidateReviewFinding(
            "finding-a",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/Foo.cs",
                originModelId: "gpt-5.4-mini",
                originLogicalModelName: "thrifty"),
            CommentSeverity.Error,
            "Potential null dereference.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12);

        var comment = Assert.Single(ReviewSynthesisExecutor.MaterializePublishedComments([finding], [Publish("finding-a", null)]));

        Assert.Equal("gpt-5.4-mini", comment.OriginModelId);
        Assert.Equal("thrifty", comment.OriginLogicalModelName);
    }

    [Fact]
    public void MaterializePublishedComments_WithNoRecordedModel_LeavesTheAttributionEmpty()
    {
        // A finding no single pass owns must not borrow one: null is what the per-model reading shows as
        // unattributed, and a guess here would be indistinguishable from a measurement.
        var comment = Assert.Single(
            ReviewSynthesisExecutor.MaterializePublishedComments(
                [Finding("finding-a", "Cross-cutting concern.")],
                [Publish("finding-a", null)]));

        Assert.Null(comment.OriginModelId);
        Assert.Null(comment.OriginLogicalModelName);
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
