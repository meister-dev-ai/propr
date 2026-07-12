// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Verification;

public sealed class PrWideCandidateFindingTests
{
    [Fact]
    public void ToCandidateReviewFinding_WithInlineAnchor_PreservesVerificationMetadata()
    {
        var candidate = new PrWideCandidateFinding(
            "candidate-001",
            "Missing DI registration still leaves the handler unresolved.",
            CandidateReviewFinding.CrossCuttingCategory,
            new ConfidenceScore("cross_file_reasoning", 84),
            new EvidenceReference([], ["src/Web/Program.cs", "src/Application/Registration.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            ["src/Web/Program.cs", "src/Application/Registration.cs"],
            CommentSeverity.Warning,
            "src/Web/Program.cs",
            42,
            "Potential DI registration gap spans multiple files.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CandidateReviewFinding.ClaimKindContextKey] = CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
                [CandidateReviewFinding.ClaimIdContextKey] = "claim-001",
            });

        var finding = candidate.ToCandidateReviewFinding(
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "pr_wide_synthesis"));

        Assert.Equal("candidate-001", finding.FindingId);
        Assert.Equal(CommentSeverity.Warning, finding.Severity);
        Assert.Equal("src/Web/Program.cs", finding.FilePath);
        Assert.Equal(42, finding.LineNumber);
        Assert.Equal("Potential DI registration gap spans multiple files.", finding.CandidateSummaryText);
        Assert.Equal(CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind, finding.InvariantCheckContext[CandidateReviewFinding.ClaimKindContextKey]);
        Assert.Equal(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, finding.Provenance.OriginKind);
    }

    [Fact]
    public void ToCandidateReviewFinding_WithoutInlineAnchor_RemainsPrLevelPublishable()
    {
        var candidate = new PrWideCandidateFinding(
            "candidate-002",
            "Cross-file ordering can publish the stale aggregate summary.",
            CandidateReviewFinding.CrossCuttingCategory,
            new ConfidenceScore("cross_file_reasoning", 76),
            new EvidenceReference([], ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"], EvidenceReference.PartialState, "pr_wide_synthesis"),
            ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"],
            CommentSeverity.Error,
            CandidateSummaryText: "Potential cross-file ordering issue noted.");

        var finding = candidate.ToCandidateReviewFinding(
            new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "pr_wide_synthesis"),
            "finding-cc-001");

        Assert.Equal("finding-cc-001", finding.FindingId);
        Assert.Equal(CommentSeverity.Error, finding.Severity);
        Assert.Null(finding.FilePath);
        Assert.Null(finding.LineNumber);
        Assert.Equal("Potential cross-file ordering issue noted.", finding.CandidateSummaryText);
        Assert.Equal(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, finding.Provenance.OriginKind);
    }

    [Fact]
    public void ToCandidateReviewFinding_ForwardsScopeRelationToConvertedFinding()
    {
        var candidate = new PrWideCandidateFinding(
            "candidate-003",
            "Publisher writes before the aggregator finishes.",
            CandidateReviewFinding.CrossCuttingCategory,
            new ConfidenceScore("cross_file_reasoning", 84),
            new EvidenceReference([], ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"],
            CommentSeverity.Warning,
            "src/Core/Aggregator.cs",
            12);

        var finding = candidate.ToCandidateReviewFinding(
            new CandidateFindingProvenance(CandidateFindingProvenance.PrWidePassOrigin, "pr_wide_pass"),
            scopeRelation: ChangedLineRelation.OnChangedLine);

        Assert.Equal(ChangedLineRelation.OnChangedLine, finding.ScopeRelation);
    }

    [Fact]
    public void ToCandidateReviewFinding_WithoutScopeRelation_LeavesRelationNull()
    {
        var candidate = new PrWideCandidateFinding(
            "candidate-004",
            "Cross-file ordering can publish the stale aggregate summary.",
            CandidateReviewFinding.CrossCuttingCategory,
            new ConfidenceScore("cross_file_reasoning", 76),
            new EvidenceReference([], ["src/Core/Aggregator.cs"], EvidenceReference.PartialState, "pr_wide_synthesis"),
            ["src/Core/Aggregator.cs"]);

        var finding = candidate.ToCandidateReviewFinding(new CandidateFindingProvenance(CandidateFindingProvenance.PrWidePassOrigin, "pr_wide_pass"));

        Assert.Null(finding.ScopeRelation);
    }
}
