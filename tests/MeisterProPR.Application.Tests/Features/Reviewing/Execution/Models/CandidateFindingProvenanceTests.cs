// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Models;

/// <summary>
///     Tests for <see cref="CandidateFindingProvenance.ResolveOriginPassKindName" /> — the finding-origin
///     resolution that backs the protocol DTO's <c>originPassKind</c>. It must use the reliable
///     <see cref="FindingProvenanceKind" /> classification, not the raw <see cref="ReviewPassKind" /> (whose
///     enum default <see cref="ReviewPassKind.Baseline" /> would mislabel synthesized/both-pass findings).
/// </summary>
public sealed class CandidateFindingProvenanceTests
{
    [Fact]
    public void ResolveOriginPassKindName_BaselineOnly_ReturnsBaseline()
    {
        var provenance = new CandidateFindingProvenance(
            CandidateFindingProvenance.PerFileCommentOrigin, "stage",
            reviewPassKind: ReviewPassKind.Baseline, findingProvenanceKind: FindingProvenanceKind.BaselineOnly);

        Assert.Equal("Baseline", provenance.ResolveOriginPassKindName());
    }

    [Fact]
    public void ResolveOriginPassKindName_ProRvOnly_ReturnsProRVAugmentation()
    {
        var provenance = new CandidateFindingProvenance(
            CandidateFindingProvenance.PerFileCommentOrigin, "stage",
            reviewPassKind: ReviewPassKind.ProRVAugmentation, findingProvenanceKind: FindingProvenanceKind.ProRVOnly);

        Assert.Equal("ProRVAugmentation", provenance.ResolveOriginPassKindName());
    }

    [Fact]
    public void ResolveOriginPassKindName_Both_ReturnsNull()
    {
        // A finding produced by both passes has no single producing pass — must not claim one.
        var provenance = new CandidateFindingProvenance(
            CandidateFindingProvenance.PerFileCommentOrigin, "stage",
            findingProvenanceKind: FindingProvenanceKind.Both);

        Assert.Null(provenance.ResolveOriginPassKindName());
    }

    [Fact]
    public void ResolveOriginPassKindName_SynthesizedCrossCutting_ReturnsNull()
    {
        // Synthesized cross-cutting findings default to FindingProvenanceKind.BaselineOnly, but they were NOT
        // produced by the baseline pass — the origin-kind check must win and yield null (regression guard:
        // the raw ReviewPassKind enum default would otherwise mislabel these as "Baseline").
        var provenance = new CandidateFindingProvenance(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, "synthesis");

        Assert.Null(provenance.ResolveOriginPassKindName());
    }
}
