// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class ReviewContextEvidenceCollectorTests
{
    [Fact]
    public async Task CollectEvidenceAsync_WithEmptyProCursorResponse_RecordsAttemptAndStatus()
    {
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Cross-file claim requires verification.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);
        var workItem = new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
            ClaimDescriptor.PrLevelStage,
            VerificationWorkItem.CrossFileScope,
            true,
            new EvidenceReference([], ["src/Foo.cs"], EvidenceReference.MissingState, "synthesis_payload"));
        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync("src/Foo.cs", "feature/x", 1, 120, Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns([new ChangedFileSummary("src/Foo.cs", ChangeType.Edit)]);
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No matches."));

        var sut = new ReviewContextEvidenceCollector();

        var bundle = await sut.CollectEvidenceAsync(workItem, reviewTools, "feature/x", CancellationToken.None);

        Assert.True(bundle.HasProCursorAttempt);
        Assert.Equal(EvidenceAttemptRecord.EmptyStatus, bundle.ProCursorResultStatus);
        Assert.Contains(bundle.EvidenceAttempts, attempt =>
            attempt.SourceFamily == EvidenceAttemptRecord.FileContentSource &&
            attempt.Status == EvidenceAttemptRecord.EmptyStatus);
        Assert.Contains(bundle.EvidenceAttempts, attempt =>
            attempt.SourceFamily == EvidenceAttemptRecord.ProCursorKnowledgeSource &&
            attempt.Status == EvidenceAttemptRecord.EmptyStatus);
    }

    [Fact]
    public async Task CollectEvidenceAsync_WithoutReviewTools_RecordsUnavailableAttempt()
    {
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-001",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Cross-file claim requires verification.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);
        var workItem = new VerificationWorkItem(
            claim,
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
            ClaimDescriptor.PrLevelStage,
            VerificationWorkItem.CrossFileScope,
            true);
        var sut = new ReviewContextEvidenceCollector();

        var bundle = await sut.CollectEvidenceAsync(workItem, null, "feature/x", CancellationToken.None);

        var attempt = Assert.Single(bundle.EvidenceAttempts);
        Assert.Equal(EvidenceAttemptRecord.RepositoryStructureSource, attempt.SourceFamily);
        Assert.Equal(EvidenceAttemptRecord.UnavailableStatus, attempt.Status);
        Assert.False(bundle.HasProCursorAttempt);
    }
}
