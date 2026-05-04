// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Verification;

public sealed class ClaimDescriptorTests
{
    [Fact]
    public void Constructor_WithPositiveAnchorLine_PreservesClaimMetadata()
    {
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "ReviewComment.Message may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            claimFamily: ClaimDescriptor.CodeContractFamily,
            anchorFilePath: "src/Foo.cs",
            anchorLineNumber: 12);

        Assert.Equal("claim-001", claim.ClaimId);
        Assert.Equal(12, claim.AnchorLineNumber);
        Assert.Equal(ClaimDescriptor.CodeContractFamily, claim.ClaimFamily);
    }

    [Fact]
    public void Constructor_WithNonPositiveAnchorLine_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "ReviewComment.Message may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            claimFamily: ClaimDescriptor.CodeContractFamily,
            anchorLineNumber: 0));
    }

    [Fact]
    public void Constructor_WithoutClaimFamily_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "ReviewComment.Message may be null.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            claimFamily: string.Empty));
    }
}
