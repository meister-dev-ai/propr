// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Verification;

public sealed class VerificationModelsTests
{
    [Fact]
    public void CreateInvariantCheckContext_UsesFirstClaimMetadata()
    {
        var claims = new[]
        {
            new ClaimDescriptor(
                "claim-001",
                "finding-001",
                ClaimDescriptor.LocalStage,
                CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
                "ReviewComment.Message may be null.",
                CommentSeverity.Warning,
                ClaimDescriptor.DeterministicOnlyMode,
                claimFamily: ClaimDescriptor.CodeContractFamily),
        };

        var context = CandidateReviewFinding.CreateInvariantCheckContext(claims);

        Assert.Equal(CandidateReviewFinding.ReviewCommentMessageNullableClaimKind, context[CandidateReviewFinding.ClaimKindContextKey]);
        Assert.Equal("claim-001", context[CandidateReviewFinding.ClaimIdContextKey]);
        Assert.Equal(ClaimDescriptor.CodeContractFamily, context[CandidateReviewFinding.ClaimFamilyContextKey]);
    }

    [Fact]
    public void VerificationWorkItem_WithMismatchedStage_Throws()
    {
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "ReviewComment.Message may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            claimFamily: ClaimDescriptor.CodeContractFamily);

        Assert.Throws<ArgumentException>(() => new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"),
            ClaimDescriptor.PrLevelStage,
            VerificationWorkItem.AnchorOnlyScope,
            false));
    }

    [Fact]
    public void VerificationWorkItem_CrossFileAiWorkItem_DefaultsGenericVerifierFamilies()
    {
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Cross-file claim.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            claimFamily: ClaimDescriptor.CrossFileConsistencyFamily);

        var workItem = new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
            ClaimDescriptor.PrLevelStage,
            VerificationWorkItem.CrossFileScope,
            true,
            existingHints: ["src/Foo.cs"]);

        Assert.Contains(VerificationWorkItem.RepositoryEvidenceVerifier, workItem.VerifierFamilies);
        Assert.Contains(VerificationWorkItem.BoundedAiVerifier, workItem.VerifierFamilies);
        Assert.Equal("src/Foo.cs", Assert.Single(workItem.ExistingHints));
    }

    [Fact]
    public void VerificationOutcome_Contradicted_BlocksPublication()
    {
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "ReviewComment.Message may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            claimFamily: ClaimDescriptor.CodeContractFamily);

        var outcome = VerificationOutcome.Contradicted(
            claim,
            InvariantFact.ReviewCommentMessageRequiredInvariantId,
            ReviewFindingGateReasonCodes.InvariantContradiction,
            "Contradicted by invariant.");

        Assert.True(outcome.BlocksPublication);
        Assert.Contains(InvariantFact.ReviewCommentMessageRequiredInvariantId, outcome.BlockedInvariantIds);
    }
}
