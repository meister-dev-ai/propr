// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

public sealed class DeterministicReviewFindingGateTests
{
    private static CandidateReviewFinding CreateFinding(
        string findingId,
        string message,
        string category,
        EvidenceReference? evidence = null,
        string? candidateSummaryText = null,
        string? filePath = null,
        int? lineNumber = null,
        VerificationOutcome? verificationOutcome = null,
        ChangedLineRelation? scopeRelation = null)
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
            evidence,
            candidateSummaryText,
            verificationOutcome: verificationOutcome,
            scopeRelation: scopeRelation);
    }

    private static CandidateReviewFinding CreatePrWideFinding(
        string findingId,
        string message,
        EvidenceReference? evidence = null,
        string? candidateSummaryText = null,
        string? filePath = null,
        int? lineNumber = null,
        VerificationOutcome? verificationOutcome = null,
        ChangedLineRelation? scopeRelation = null,
        IReadOnlyDictionary<string, string>? invariantCheckContext = null)
    {
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(CandidateFindingProvenance.PrWidePassOrigin, "pr_wide_pass"),
            CommentSeverity.Warning,
            message,
            CandidateReviewFinding.CrossCuttingCategory,
            filePath,
            lineNumber,
            evidence,
            candidateSummaryText,
            invariantCheckContext,
            verificationOutcome,
            scopeRelation);
    }

    [Fact]
    public async Task EvaluateAsync_PrWideVerifierDeclinedWithResolvedEvidenceAndAnchor_AssignsSummaryOnly()
    {
        // Regression guard: resolved multi-file evidence must NOT override a declining verifier. Only the bounded
        // PR-verifier's Publish verdict earns publication, so a declining verdict stays summary-only even with two
        // supporting files and a changed-line anchor.
        var sut = new DeterministicReviewFindingGate();
        var outcome = new VerificationOutcome(
            "claim-prw-001",
            "finding-prw-02-001",
            VerificationOutcome.UnresolvedKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.WeakEvidence,
            "The retrieved repository evidence did not independently support the cross-file claim.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);

        var finding = CreatePrWideFinding(
            "finding-prw-02-001",
            "Publisher writes before the aggregator finishes, also affecting src/Api/PublishController.cs.",
            new EvidenceReference([], ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            "Potential cross-file ordering issue noted.",
            "src/Core/Aggregator.cs",
            12,
            outcome,
            ChangedLineRelation.OnChangedLine);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, decision.ReasonCodes);
        Assert.Equal("pr_wide_summary_rules", decision.RuleSource);
        Assert.NotNull(decision.SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_PrWideVerifierPublishWithAdjacentAnchor_AssignsPublish()
    {
        var sut = new DeterministicReviewFindingGate();
        var outcome = new VerificationOutcome(
            "claim-prw-001",
            "finding-prw-02-002",
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.StrongEvidence,
            "The retrieved repository evidence supports the cross-file claim.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);

        var finding = CreatePrWideFinding(
            "finding-prw-02-002",
            "Cross-file registration ordering can still publish stale results.",
            filePath: "src/Core/Aggregator.cs",
            lineNumber: 12,
            verificationOutcome: outcome,
            scopeRelation: ChangedLineRelation.AdjacentToChange);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.PublishDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.PrWideAnchoredVerifiedClaim, decision.ReasonCodes);
        Assert.Equal("pr_wide_anchored_verified_rules", decision.RuleSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ChangedLineRelation.OutsideChange)]
    public async Task EvaluateAsync_PrWideVerifierPublishWithoutInScopeAnchor_AssignsSummaryOnly(ChangedLineRelation? scopeRelation)
    {
        // The verifier approved publication, so the ONLY disqualifier is the missing changed-line anchor. The
        // reason code must distinguish this location shortfall from a verification gap.
        var sut = new DeterministicReviewFindingGate();
        var outcome = new VerificationOutcome(
            "claim-prw-003",
            "finding-prw-02-003",
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.StrongEvidence,
            "The retrieved repository evidence supports the cross-file claim.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);

        var finding = CreatePrWideFinding(
            "finding-prw-02-003",
            "Cross-file concern spanning multiple files.",
            new EvidenceReference([], ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            "Potential cross-file ordering issue noted.",
            "src/Core/Aggregator.cs",
            244,
            outcome,
            scopeRelation);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.PrWideMissingChangedLineAnchor, decision.ReasonCodes);
        Assert.Equal("pr_wide_summary_rules", decision.RuleSource);
        Assert.NotNull(decision.SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_PrWideLocatedButNotEarned_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreatePrWideFinding(
            "finding-prw-02-004",
            "Cross-file concern that lacks resolved multi-file evidence.",
            new EvidenceReference([], ["src/Core/Aggregator.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            "Potential cross-file ordering issue noted.",
            "src/Core/Aggregator.cs",
            12,
            scopeRelation: ChangedLineRelation.OnChangedLine);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, decision.ReasonCodes);
        Assert.Equal("pr_wide_summary_rules", decision.RuleSource);
        Assert.NotNull(decision.SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_PrWideContradictingInvariant_AssignsDrop()
    {
        var sut = new DeterministicReviewFindingGate();
        var invariantFacts = new[]
        {
            new InvariantFact(
                InvariantFact.ReviewCommentMessageRequiredInvariantId,
                InvariantFact.DomainFamily,
                "Review comment message required",
                "domain_model",
                "true",
                "A review comment message is always required."),
        };
        var finding = CreatePrWideFinding(
            "finding-prw-02-005",
            "ReviewComment.Message may be null when the model omits a message.",
            new EvidenceReference([], ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"),
            filePath: "src/Core/Aggregator.cs",
            lineNumber: 12,
            scopeRelation: ChangedLineRelation.OnChangedLine,
            invariantCheckContext: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CandidateReviewFinding.ClaimKindContextKey] = CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            });

        var decision = Assert.Single(await sut.EvaluateAsync([finding], invariantFacts));

        Assert.Equal(FinalGateDecision.DropDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.InvariantContradiction, decision.ReasonCodes);
        Assert.Equal("invariant_contradiction_rules", decision.RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_NonPrWideCrossCuttingWithResolvedEvidenceAndAnchor_RemainsSummaryOnly()
    {
        // Precision guard: a file-by-file (non-PR-wide) cross-cutting finding stays summary-only even with
        // resolved multi-file evidence and an in-scope anchor. The PR-wide publish branch must not touch it.
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-cc-regression",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "synthesis_payload"),
            "Potential DI registration gap spans multiple files.",
            filePath: "src/Foo.cs",
            lineNumber: 12,
            scopeRelation: ChangedLineRelation.OnChangedLine);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, decision.ReasonCodes);
        Assert.Equal("cross_cutting_claim_support_rules", decision.RuleSource);
        Assert.DoesNotContain(ReviewFindingGateReasonCodes.PrWideAnchoredVerifiedClaim, decision.ReasonCodes);
    }

    [Fact]
    public async Task EvaluateAsync_OutsideChangeDefaultPublish_AttachesReasonCodeWithoutChangingDisposition()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-pf-001",
            "Null dereference in pre-existing helper.",
            CandidateReviewFinding.PerFileCommentCategory,
            filePath: "src/Foo.cs",
            lineNumber: 244,
            scopeRelation: ChangedLineRelation.OutsideChange);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.PublishDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.DefaultPublish, decision.ReasonCodes);
        Assert.Contains(ReviewFindingGateReasonCodes.OutsideChangedLines, decision.ReasonCodes);
        Assert.Equal("default_publish_rules", decision.RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_OutsideChangeSummaryOnly_AttachesReasonCodeAndKeepsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-arch-001",
            "Architecture concerns span the PR and should be revisited.",
            "architecture",
            candidateSummaryText: "Potential architecture concern noted.",
            filePath: "src/Foo.cs",
            lineNumber: 244,
            scopeRelation: ChangedLineRelation.OutsideChange);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.WeakBroadFinding, decision.ReasonCodes);
        Assert.Contains(ReviewFindingGateReasonCodes.OutsideChangedLines, decision.ReasonCodes);
        Assert.NotNull(decision.SummaryText);
    }

    [Theory]
    [InlineData(ChangedLineRelation.OnChangedLine)]
    [InlineData(ChangedLineRelation.AdjacentToChange)]
    public async Task EvaluateAsync_InScopeRelation_DoesNotAttachOutsideChangedLinesReasonCode(ChangedLineRelation relation)
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-pf-002",
            "Null dereference on the changed line.",
            CandidateReviewFinding.PerFileCommentCategory,
            filePath: "src/Foo.cs",
            lineNumber: 12,
            scopeRelation: relation);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.PublishDisposition, decision.Disposition);
        Assert.DoesNotContain(ReviewFindingGateReasonCodes.OutsideChangedLines, decision.ReasonCodes);
    }

    [Fact]
    public async Task EvaluateAsync_NullScopeRelation_DoesNotAttachOutsideChangedLinesReasonCode()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-pf-003",
            "Null dereference with unknown scope.",
            CandidateReviewFinding.PerFileCommentCategory,
            filePath: "src/Foo.cs",
            lineNumber: 12);

        var decision = Assert.Single(await sut.EvaluateAsync([finding], []));

        Assert.Equal(FinalGateDecision.PublishDisposition, decision.Disposition);
        Assert.DoesNotContain(ReviewFindingGateReasonCodes.OutsideChangedLines, decision.ReasonCodes);
    }

    [Fact]
    public async Task EvaluateAsync_CrossCuttingWithoutResolvedMultiFileEvidence_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-cc-001",
            "Potential cross-cutting concern.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], [], EvidenceReference.MissingState, "synthesis_payload"),
            "Potential cross-cutting concern noted, but the available evidence did not support publication as a review thread.");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingMultiFileEvidence, decisions[0].ReasonCodes);
        Assert.Equal("cross_cutting_evidence_rules", decisions[0].RuleSource);
        Assert.NotNull(decisions[0].SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_CrossCuttingWithResolvedMultiFileEvidence_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-cc-001",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference(
                ["finding-pf-001", "finding-pf-002"],
                ["src/Foo.cs", "src/Bar.cs"],
                EvidenceReference.ResolvedState,
                "synthesis_payload"),
            "Potential DI registration gap spans multiple files.");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, decisions[0].ReasonCodes);
        Assert.Equal("cross_cutting_claim_support_rules", decisions[0].RuleSource);
        Assert.NotNull(decisions[0].SummaryText);
    }

    [Fact]
    public async Task EvaluateAsync_BroadWeakArchitectureFinding_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-arch-001",
            "Architecture concerns span the PR and should be revisited.",
            "architecture",
            null,
            "Potential architecture concern noted, but it did not meet the publication bar for an actionable review thread.");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.WeakBroadFinding, decisions[0].ReasonCodes);
        Assert.Equal("broad_finding_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_NonActionableFinding_AssignsDrop()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = CreateFinding(
            "finding-na-001",
            "Consider refactoring this area for clarity.",
            "non_actionable");

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.NonActionableFinding, decisions[0].ReasonCodes);
        Assert.Equal("non_actionable_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_VerificationOutcomeSummaryOnly_AssignsSummaryOnly()
    {
        var sut = new DeterministicReviewFindingGate();
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-verify-001",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Missing DI registration in multiple files.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);

        var outcome = new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.UnresolvedKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
            [],
            VerificationOutcome.WeakEvidence,
            "Evidence collection did not independently support publication.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);

        var finding = CreateFinding(
            "finding-verify-001",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.MissingState, "synthesis_payload"),
            "Potential DI registration gap spans multiple files.",
            verificationOutcome: outcome);

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.MissingMultiFileEvidence, decisions[0].ReasonCodes);
        Assert.Equal("verification_outcome_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_VerificationOutcomePublish_AssignsPublish()
    {
        var sut = new DeterministicReviewFindingGate();
        var claim = new ClaimDescriptor(
            "claim-001",
            "finding-verify-002",
            ClaimDescriptor.PrLevelStage,
            CandidateReviewFinding.CrossFileEvidenceRequiredClaimKind,
            "Missing DI registration in multiple files.",
            CommentSeverity.Warning,
            ClaimDescriptor.NeedsEvidenceMode,
            ClaimDescriptor.CrossFileConsistencyFamily,
            requiresCrossFileEvidence: true);

        var outcome = new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.StrongEvidence,
            "Bounded claim support was independently verified.",
            VerificationOutcome.CombinedEvaluator,
            false);

        var finding = CreateFinding(
            "finding-verify-002",
            "Missing DI registration in multiple files.",
            CandidateReviewFinding.CrossCuttingCategory,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "synthesis_payload"),
            "Potential DI registration gap spans multiple files.",
            verificationOutcome: outcome);

        var decisions = await sut.EvaluateAsync([finding], []);

        Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.PublishDisposition, decisions[0].Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, decisions[0].ReasonCodes);
        Assert.Equal("verification_outcome_rules", decisions[0].RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_InvestigationOriginWithoutSupport_AssignsDrop()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = new CandidateReviewFinding(
            "finding-agentic-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.DeeperFollowUpOrigin,
                "agentic_file_investigation",
                "src/Foo.cs",
                evidenceSetId: "evidence-001",
                requiresExplicitSupport: true,
                sourceOriginId: "task-001"),
            CommentSeverity.Warning,
            "Missing DI registration in multiple files.",
            "architecture",
            "src/Foo.cs",
            12,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "agentic_file_investigation"),
            "Potential DI registration gap spans multiple files.");

        var decisions = await sut.EvaluateAsync([finding], []);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.InvestigationOriginMissingExplicitSupport, decision.ReasonCodes);
        Assert.Equal("investigation_missing_support_rules", decision.RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_InvestigationOriginWithSupport_AssignsPublish()
    {
        var sut = new DeterministicReviewFindingGate();
        var claim = new ClaimDescriptor(
            "claim-agentic-001",
            "finding-agentic-002",
            ClaimDescriptor.LocalStage,
            CandidateReviewFinding.DockerFinalStageRootUserClaimKind,
            "The final Docker stage runs as root because a runtime USER directive is missing.",
            CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode,
            ClaimDescriptor.OperationalRiskFamily,
            anchorFilePath: "Dockerfile",
            anchorLineNumber: 8);

        var outcome = new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.StrongEvidence,
            "Deterministic verifier confirmed the objective follow-up claim.",
            VerificationOutcome.DeterministicRulesEvaluator,
            false);

        var finding = new CandidateReviewFinding(
            "finding-agentic-002",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.DeeperFollowUpOrigin,
                "agentic_file_investigation",
                "Dockerfile",
                evidenceSetId: "evidence-002",
                requiresExplicitSupport: true,
                sourceOriginId: "task-002"),
            CommentSeverity.Warning,
            "The final Docker stage runs as root because a runtime USER directive is missing.",
            CandidateReviewFinding.PerFileCommentCategory,
            "Dockerfile",
            8,
            verificationOutcome: outcome);

        var decisions = await sut.EvaluateAsync([finding], []);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.PublishDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, decision.ReasonCodes);
        Assert.Equal("investigation_verified_support_rules", decision.RuleSource);
    }

    [Fact]
    public async Task EvaluateAsync_RepeatedJudgmentAgreementWithoutExplicitSupport_AssignsPublish()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = new CandidateReviewFinding(
            "finding-rj-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.RepeatedJudgmentOrigin,
                "repeated_judgment",
                "src/Foo.cs",
                evidenceSetId: "evidence-task-001",
                requiresExplicitSupport: true,
                sourceOriginId: "repeated-judgment-candidate-001"),
            CommentSeverity.Warning,
            "Missing DI registration in multiple files.",
            "architecture",
            "src/Foo.cs",
            12,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "agentic_file_investigation"),
            "Potential DI registration gap spans multiple files.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repeatedJudgmentAgreementState"] = "Agreed",
                ["repeatedJudgmentUsedSameEvidenceSet"] = "true",
            });

        var decisions = await sut.EvaluateAsync([finding], []);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.PublishDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, decision.ReasonCodes);
    }

    [Fact]
    public async Task EvaluateAsync_RepeatedJudgmentDisagreementWithoutExplicitSupport_AssignsDrop()
    {
        var sut = new DeterministicReviewFindingGate();
        var finding = new CandidateReviewFinding(
            "finding-rj-002",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.RepeatedJudgmentOrigin,
                "repeated_judgment",
                "src/Foo.cs",
                evidenceSetId: "evidence-task-001",
                requiresExplicitSupport: true,
                sourceOriginId: "repeated-judgment-candidate-002"),
            CommentSeverity.Warning,
            "Missing DI registration in multiple files.",
            "architecture",
            "src/Foo.cs",
            12,
            new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "agentic_file_investigation"),
            "Potential DI registration gap spans multiple files.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repeatedJudgmentAgreementState"] = "Disagreed",
                ["repeatedJudgmentUsedSameEvidenceSet"] = "true",
            });

        var decisions = await sut.EvaluateAsync([finding], []);

        var decision = Assert.Single(decisions);
        Assert.Equal(FinalGateDecision.DropDisposition, decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.RepeatedJudgmentDisagreement, decision.ReasonCodes);
    }
}
