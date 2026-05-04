// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class DeterministicReviewClaimExtractorTests
{
    private static CandidateReviewFinding CreateFinding(
        string findingId,
        string message,
        string category,
        EvidenceReference? evidence = null,
        string? filePath = "src/Foo.cs",
        int? lineNumber = 12)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                category == CandidateReviewFinding.CrossCuttingCategory
                    ? CandidateFindingProvenance.SynthesizedCrossCuttingOrigin
                    : CandidateFindingProvenance.PerFileCommentOrigin,
                category == CandidateReviewFinding.CrossCuttingCategory ? "synthesis" : "per_file_review",
                filePath),
            CommentSeverity.Warning,
            message,
            category,
            filePath,
            lineNumber,
            evidence);
    }

    [Fact]
    public void ExtractClaims_KnownInvariantBackedClaim_AssignsGenericCodeContractFamily()
    {
        var sut = new DeterministicReviewClaimExtractor();
        var finding = CreateFinding(
            "finding-001",
            "ReviewComment.Message may be null when the model omits a message.",
            CandidateReviewFinding.PerFileCommentCategory);

        var claims = sut.ExtractClaims(finding);

        var claim = Assert.Single(claims);
        Assert.Equal(CandidateReviewFinding.ReviewCommentMessageNullableClaimKind, claim.ClaimKind);
        Assert.Equal(ClaimDescriptor.CodeContractFamily, claim.ClaimFamily);
        Assert.Equal(ClaimDescriptor.LocalStage, claim.Stage);
    }

    [Fact]
    public void ExtractClaims_SymbolMissingFinding_AssignsApiUsageFamilyAndNeedsEvidence()
    {
        var sut = new DeterministicReviewClaimExtractor();
        var finding = CreateFinding(
            "finding-001a",
            "The helper method isCommentRelevanceEvent is missing and will fail at runtime.",
            CandidateReviewFinding.PerFileCommentCategory);

        var claims = sut.ExtractClaims(finding);

        var claim = Assert.Single(claims);
        Assert.Equal(CandidateReviewFinding.GenericReviewAssertionClaimKind, claim.ClaimKind);
        Assert.Equal(ClaimDescriptor.ApiOrSymbolUsageFamily, claim.ClaimFamily);
        Assert.Equal(ClaimDescriptor.NeedsEvidenceMode, claim.VerificationMode);
        Assert.True(claim.RequiresSymbolEvidence);
    }

    [Fact]
    public void ExtractClaims_CrossCuttingFinding_AssignsGenericCrossFileConsistencyFamily()
    {
        var sut = new DeterministicReviewClaimExtractor();
        var finding = CreateFinding(
            "finding-002",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference(
                ["finding-pf-001", "finding-pf-002"],
                ["src/Foo.cs", "src/Bar.cs"],
                EvidenceReference.MissingState,
                "synthesis_payload"),
            null,
            null);

        var claims = sut.ExtractClaims(finding);

        var claim = Assert.Single(claims);
        Assert.Equal(CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind, claim.ClaimKind);
        Assert.Equal(ClaimDescriptor.CrossFileConsistencyFamily, claim.ClaimFamily);
        Assert.Equal(ClaimDescriptor.PrLevelStage, claim.Stage);
        Assert.True(claim.RequiresCrossFileEvidence);
    }

    [Fact]
    public void ExtractClaims_GenericArchitectureFinding_FallsBackToClaimFamilyScaffolding()
    {
        var sut = new DeterministicReviewClaimExtractor();
        var finding = CreateFinding(
            "finding-003",
            "The wiring across the PR is inconsistent and may leave handlers unregistered.",
            "architecture",
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.MissingState, "synthesis_payload"),
            null,
            null);

        var claims = sut.ExtractClaims(finding);

        var claim = Assert.Single(claims);
        Assert.Equal(CandidateReviewFinding.GenericReviewAssertionClaimKind, claim.ClaimKind);
        Assert.Equal(ClaimDescriptor.CrossFileConsistencyFamily, claim.ClaimFamily);
        Assert.Equal(ClaimDescriptor.PrLevelStage, claim.Stage);
    }
}
