// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class FileByFileReviewOrchestratorSynthesisParsingTests
{
    [Fact]
    public void TryParseSynthesisResponse_WithStructuredCrossCuttingEvidence_ParsesCandidateFindings()
    {
        const string json =
            """
            {
              "summary": "Overall summary.",
              "cross_cutting_concerns": [
                {
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Potential DI registration gap spans multiple files.",
                  "supportingFindingIds": ["finding-pf-001", "finding-pf-002"],
                  "supportingFiles": ["src/Foo.cs", "src/Bar.cs"],
                  "evidenceResolutionState": "resolved",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        var parsed = FileByFileReviewOrchestrator.TryParseSynthesisResponse(json, out var summary, out var findings);

        Assert.True(parsed);
        Assert.Equal("Overall summary.", summary);
        Assert.Single(findings);

        var finding = findings[0];
        Assert.Equal("Missing DI registration in multiple files.", finding.Message);
        Assert.Equal(CommentSeverity.Warning, finding.Severity);
        Assert.Equal(CandidateReviewFinding.CrossCuttingCategory, finding.Category);
        Assert.Equal(CandidateFindingProvenance.SynthesizedCrossCuttingOrigin, finding.Provenance.OriginKind);
        Assert.Equal("synthesis", finding.Provenance.GeneratedByStage);
        Assert.Equal("Potential DI registration gap spans multiple files.", finding.CandidateSummaryText);
        Assert.NotNull(finding.Evidence);
        Assert.Equal(EvidenceReference.ResolvedState, finding.Evidence!.EvidenceResolutionState);
        Assert.Equal("synthesis_payload", finding.Evidence.EvidenceSource);
        Assert.Equal(["finding-pf-001", "finding-pf-002"], finding.Evidence.SupportingFindingIds);
        Assert.Equal(["src/Foo.cs", "src/Bar.cs"], finding.Evidence.SupportingFiles);
    }

    [Fact]
    public void TryParseSynthesisResponse_WithMinimalCrossCuttingConcern_DefaultsMissingEvidenceToMissingState()
    {
        const string json =
            """
            {
              "summary": "Overall summary.",
              "cross_cutting_concerns": [
                {
                  "message": "Consider refactoring the architecture boundary.",
                  "severity": "warning"
                }
              ]
            }
            """;

        var parsed = FileByFileReviewOrchestrator.TryParseSynthesisResponse(json, out _, out var findings);

        Assert.True(parsed);
        Assert.Single(findings);
        Assert.NotNull(findings[0].Evidence);
        var evidence = findings[0].Evidence!;
        Assert.Equal(EvidenceReference.MissingState, evidence.EvidenceResolutionState);
        Assert.Empty(evidence.SupportingFindingIds);
        Assert.Empty(evidence.SupportingFiles);
    }
}
