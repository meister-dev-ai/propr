// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

public sealed class DeterministicReviewFindingGateInvariantTests
{
    [Fact]
    public async Task EvaluateAsync_MessageNullabilityContradiction_BlocksPublicationWithDomainInvariant()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = new CandidateReviewFinding(
            "finding-invariant-001",
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
            CommentSeverity.Warning,
            "ReviewComment.Message may be null when the model omits a message.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12,
            invariantCheckContext: new Dictionary<string, string>
            {
                ["claimKind"] = "review_comment_message_nullable",
            });

        var decisions = await sut.EvaluateAsync(
            [finding],
            [
                new InvariantFact(
                    DomainReviewInvariantFactProvider.ReviewCommentMessageRequiredInvariantId,
                    InvariantFact.DomainFamily,
                    "ReviewComment.Message required",
                    "ReviewComment constructor semantics",
                    "message_non_null_and_non_empty",
                    "ReviewComment requires a non-null, non-empty message value."),
            ]);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.InvariantContradiction, decision.ReasonCodes);
        Assert.Equal("invariant_contradiction_rules", decision.RuleSource);
        Assert.Contains(DomainReviewInvariantFactProvider.ReviewCommentMessageRequiredInvariantId, decision.BlockedInvariantIds);
    }

    [Fact]
    public async Task EvaluateAsync_ReviewFileResultsUniquenessContradiction_BlocksPublicationWithPersistenceInvariant()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = new CandidateReviewFinding(
            "finding-invariant-002",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/MeisterProPR.Infrastructure/AI/FileByFileReviewOrchestrator.cs"),
            CommentSeverity.Warning,
            "Duplicate review_file_results rows for the same job and file path are expected during retry.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/MeisterProPR.Infrastructure/AI/FileByFileReviewOrchestrator.cs",
            200,
            invariantCheckContext: new Dictionary<string, string>
            {
                ["claimKind"] = "review_file_results_duplicate_expected",
            });

        var decisions = await sut.EvaluateAsync(
            [finding],
            [
                new InvariantFact(
                    PersistenceReviewInvariantFactProvider.ReviewFileResultsUniqueJobPathInvariantId,
                    InvariantFact.PersistenceFamily,
                    "Review file result uniqueness",
                    "EF metadata / review_file_results unique index",
                    "unique(job_id,file_path)",
                    "The review_file_results table enforces a unique row per (job_id, file_path)."),
            ]);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.InvariantContradiction, decision.ReasonCodes);
        Assert.Equal("invariant_contradiction_rules", decision.RuleSource);
        Assert.Contains(PersistenceReviewInvariantFactProvider.ReviewFileResultsUniqueJobPathInvariantId, decision.BlockedInvariantIds);
    }

    [Fact]
    public async Task EvaluateAsync_ReviewResultCommentsNullabilityContradiction_BlocksPublicationWithDomainInvariant()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = new CandidateReviewFinding(
            "finding-invariant-003",
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
            CommentSeverity.Warning,
            "The review result comments collection may be null when no findings are returned.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            20,
            invariantCheckContext: new Dictionary<string, string>
            {
                [CandidateReviewFinding.ClaimKindContextKey] = CandidateReviewFinding.ReviewResultCommentsNullableClaimKind,
            });

        var decisions = await sut.EvaluateAsync(
            [finding],
            [
                new InvariantFact(
                    DomainReviewInvariantFactProvider.ReviewResultCommentsRequiredInvariantId,
                    InvariantFact.DomainFamily,
                    "ReviewResult.Comments required",
                    "ReviewResult constructor semantics",
                    "comments_collection_non_null",
                    "ReviewResult requires a non-null comments collection value."),
            ]);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.InvariantContradiction, decision.ReasonCodes);
        Assert.Equal("invariant_contradiction_rules", decision.RuleSource);
        Assert.Contains(DomainReviewInvariantFactProvider.ReviewResultCommentsRequiredInvariantId, decision.BlockedInvariantIds);
    }
}
