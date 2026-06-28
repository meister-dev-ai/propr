// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class CandidateFindingFactoryTests
{
    [Fact]
    public void Build_WithDuplicateOverrideComments_ReusesOriginalFindingsInOrder()
    {
        var comment = new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Repeated warning.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment, comment]);
        var sut = new CandidateFindingFactory(null);

        var findings = sut.Build([fileResult], [comment, comment]);

        Assert.Equal(2, findings.Count);
        Assert.Equal(FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, 1), findings[0].FindingId);
        Assert.Equal(FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, 2), findings[1].FindingId);
        Assert.All(findings, finding => Assert.Equal(CandidateFindingProvenance.PerFileCommentOrigin, finding.Provenance.OriginKind));
    }

    [Fact]
    public void Build_WithCrossFileDerivedComment_CreatesResolvedDerivedFinding()
    {
        var sut = new CandidateFindingFactory(null);

        var findings = sut.Build(
            [],
            [
                new ReviewComment(
                    null,
                    null,
                    CommentSeverity.Warning,
                    "[Cross-file] Shared issue across services. Affected files: src/Foo.cs, src/Bar.cs, src/Foo.cs"),
            ]);

        var finding = Assert.Single(findings);
        Assert.Equal("finding-dedup-001", finding.FindingId);
        Assert.Equal(CandidateReviewFinding.CrossCuttingCategory, finding.Category);
        Assert.Null(finding.FilePath);
        Assert.Null(finding.LineNumber);
        Assert.Equal("Cross-file concern derived from multiple per-file findings.", finding.CandidateSummaryText);

        Assert.NotNull(finding.Evidence);
        var evidence = finding.Evidence!;
        Assert.Equal(EvidenceReference.ResolvedState, evidence.EvidenceResolutionState);
        Assert.Equal("derived_from_per_file_findings", evidence.EvidenceSource);
        Assert.Equal(["src/Foo.cs", "src/Bar.cs"], evidence.SupportingFiles);
    }

    [Fact]
    public void Build_WithClaimExtractor_PopulatesInvariantCheckContextAndNormalizesLineNumber()
    {
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>())
            .Returns(callInfo =>
            {
                var finding = callInfo.Arg<CandidateReviewFinding>();
                return
                [
                    new ClaimDescriptor(
                        $"claim-{finding.FindingId}",
                        finding.FindingId,
                        ClaimDescriptor.LocalStage,
                        CandidateReviewFinding.GenericReviewAssertionClaimKind,
                        finding.Message,
                        finding.Severity,
                        ClaimDescriptor.DeterministicOnlyMode,
                        ClaimDescriptor.CodeContractFamily),
                ];
            });

        var fileResult = CreateCompletedFileResult(
            "src/Foo.cs",
            [new ReviewComment("src/Foo.cs", 0, CommentSeverity.Warning, "Potential issue requires review.")]);
        var sut = new CandidateFindingFactory(extractor);

        var finding = Assert.Single(sut.Build([fileResult]));

        Assert.Null(finding.LineNumber);
        Assert.Equal(CandidateReviewFinding.GenericReviewAssertionClaimKind, finding.InvariantCheckContext[CandidateReviewFinding.ClaimKindContextKey]);
        Assert.Equal($"claim-{finding.FindingId}", finding.InvariantCheckContext[CandidateReviewFinding.ClaimIdContextKey]);
        Assert.Equal(ClaimDescriptor.CodeContractFamily, finding.InvariantCheckContext[CandidateReviewFinding.ClaimFamilyContextKey]);
        Assert.Equal("1", finding.InvariantCheckContext[CandidateReviewFinding.ClaimCountContextKey]);
    }

    [Fact]
    public void AssignSynthesisFindingIds_AssignsSequentialIdsWithoutChangingPayload()
    {
        CandidateReviewFinding[] synthesized =
        [
            new(
                "finding-cc-unassigned-001",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Warning,
                "Cross-file issue one.",
                CandidateReviewFinding.CrossCuttingCategory,
                evidence: new EvidenceReference([], ["src/Foo.cs", "src/Bar.cs"], EvidenceReference.ResolvedState, "synthesis_payload"),
                candidateSummaryText: "Issue one summary."),
            new(
                "finding-cc-unassigned-002",
                new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis"),
                CommentSeverity.Error,
                "Cross-file issue two.",
                CandidateReviewFinding.CrossCuttingCategory),
        ];

        var assigned = CandidateFindingFactory.AssignSynthesisFindingIds(synthesized);

        Assert.Equal("finding-cc-001", assigned[0].FindingId);
        Assert.Equal("finding-cc-002", assigned[1].FindingId);
        Assert.Equal("Issue one summary.", assigned[0].CandidateSummaryText);
        Assert.Equal(CommentSeverity.Error, assigned[1].Severity);
        Assert.Equal(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, assigned[0].Provenance.OriginKind);
    }

    [Fact]
    public void MergeFindings_AugmentationOnlyFinding_IsTaggedAsProRvOnly()
    {
        var augmentationFinding = new CandidateReviewFinding(
            "finding-aug-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/Foo.cs",
                reviewPassKind: ReviewPassKind.ProRVAugmentation,
                findingProvenanceKind: FindingProvenanceKind.ProRVOnly),
            CommentSeverity.Warning,
            "Augmentation-only issue.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12);

        var merged = CandidateFindingFactory.MergeFindings([], [augmentationFinding]);

        var finding = Assert.Single(merged);
        Assert.NotNull(finding.MergedFinding);
        Assert.Equal(FindingProvenanceKind.ProRVOnly, finding.MergedFinding!.Provenance);
        Assert.Equal([ReviewPassKind.ProRVAugmentation], finding.MergedFinding.SourcePasses);
    }

    [Fact]
    public void MergeFindings_ExactBaselineAndAugmentationMatch_MergesToBoth()
    {
        var baselineFinding = new CandidateReviewFinding(
            "finding-base-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/Foo.cs",
                reviewPassKind: ReviewPassKind.Baseline,
                findingProvenanceKind: FindingProvenanceKind.BaselineOnly),
            CommentSeverity.Warning,
            "Shared issue.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12);
        var augmentationFinding = new CandidateReviewFinding(
            "finding-aug-001",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                "src/Foo.cs",
                reviewPassKind: ReviewPassKind.ProRVAugmentation,
                findingProvenanceKind: FindingProvenanceKind.ProRVOnly),
            CommentSeverity.Warning,
            "Shared issue.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12);

        var merged = CandidateFindingFactory.MergeFindings([baselineFinding], [augmentationFinding]);

        var finding = Assert.Single(merged);
        Assert.NotNull(finding.MergedFinding);
        Assert.Equal(FindingProvenanceKind.Both, finding.MergedFinding!.Provenance);
        Assert.Equal([ReviewPassKind.Baseline, ReviewPassKind.ProRVAugmentation], finding.MergedFinding.SourcePasses);
    }

    [Fact]
    public void Build_FindingOnChangedLine_ClassifiesAsOnChangedLine()
    {
        var comment = new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "On a changed line.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment]);
        var ranges = RangesByPath("src/Foo.cs", (10, 14));
        var sut = new CandidateFindingFactory(null);

        var finding = Assert.Single(sut.Build([fileResult], changedLineRangesByPath: ranges));

        Assert.Equal(ChangedLineRelation.OnChangedLine, finding.ScopeRelation);
    }

    [Fact]
    public void Build_FindingPathHasLeadingSlash_StillClassifiesAgainstRepoRelativeRanges()
    {
        // The AI emits comment file paths that can carry a leading slash (e.g. "/src/Foo.cs"), while the
        // changed-range lookup is keyed by the repository-relative path. Classification must normalize both
        // sides — otherwise every lookup misses and scope labeling silently no-ops in production.
        var comment = new ReviewComment("/src/Foo.cs", 12, CommentSeverity.Warning, "Leading-slash path.");
        var fileResult = CreateCompletedFileResult("/src/Foo.cs", [comment]);
        var ranges = RangesByPath("src/Foo.cs", (10, 14));
        var sut = new CandidateFindingFactory(null);

        var finding = Assert.Single(sut.Build([fileResult], changedLineRangesByPath: ranges));

        Assert.Equal(ChangedLineRelation.OnChangedLine, finding.ScopeRelation);
    }

    [Fact]
    public void Build_FindingWithinToleranceOfChangedRange_ClassifiesAsAdjacent()
    {
        var comment = new ReviewComment("src/Foo.cs", 17, CommentSeverity.Warning, "Just past the edit.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment]);
        var ranges = RangesByPath("src/Foo.cs", (10, 14));
        var sut = new CandidateFindingFactory(null);

        var finding = Assert.Single(sut.Build([fileResult], changedLineRangesByPath: ranges));

        Assert.Equal(ChangedLineRelation.AdjacentToChange, finding.ScopeRelation);
    }

    [Fact]
    public void Build_FindingFarFromChangedRange_ClassifiesAsOutsideChange()
    {
        var comment = new ReviewComment("src/Foo.cs", 244, CommentSeverity.Warning, "Pre-existing defect far from the edit.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment]);
        var ranges = RangesByPath("src/Foo.cs", (120, 130));
        var sut = new CandidateFindingFactory(null);

        var finding = Assert.Single(sut.Build([fileResult], changedLineRangesByPath: ranges));

        Assert.Equal(ChangedLineRelation.OutsideChange, finding.ScopeRelation);
    }

    [Fact]
    public void Build_FindingWithNullLine_HasNoScopeRelation()
    {
        var comment = new ReviewComment("src/Foo.cs", null, CommentSeverity.Warning, "Unknown line.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment]);
        var ranges = RangesByPath("src/Foo.cs", (10, 14));
        var sut = new CandidateFindingFactory(null);

        var finding = Assert.Single(sut.Build([fileResult], changedLineRangesByPath: ranges));

        Assert.Null(finding.ScopeRelation);
    }

    [Fact]
    public void Build_FileWithNoResolvableRanges_HasNoScopeRelation()
    {
        var comment = new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "No ranges for this file.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment]);
        var sut = new CandidateFindingFactory(null);

        // No entry for the file path means the diff yielded no resolvable ranges.
        var finding = Assert.Single(sut.Build([fileResult], changedLineRangesByPath: RangesByPath("src/Other.cs", (1, 5))));

        Assert.Null(finding.ScopeRelation);
    }

    [Fact]
    public void Build_WithoutRangeLookup_HasNoScopeRelation()
    {
        var comment = new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "No lookup supplied.");
        var fileResult = CreateCompletedFileResult("src/Foo.cs", [comment]);
        var sut = new CandidateFindingFactory(null);

        var finding = Assert.Single(sut.Build([fileResult]));

        Assert.Null(finding.ScopeRelation);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>> RangesByPath(
        string path,
        params (int Start, int End)[] ranges)
    {
        return new Dictionary<string, IReadOnlyList<(int Start, int End)>>(StringComparer.Ordinal)
        {
            [path] = ranges,
        };
    }

    private static ReviewFileResult CreateCompletedFileResult(string filePath, IReadOnlyList<ReviewComment> comments)
    {
        var fileResult = new ReviewFileResult(Guid.NewGuid(), filePath);
        fileResult.MarkCompleted("file summary", comments);
        return fileResult;
    }
}
