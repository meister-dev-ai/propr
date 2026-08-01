// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class SynthesisResponseParserTests
{
    [Fact]
    public void TryParse_WithMarkdownFencedJson_ParsesSummaryAndDefaultsEvidenceMetadata()
    {
        const string payload = """
                               ```json
                               {
                                 "summary": "Overall summary.",
                                 "cross_cutting_concerns": [
                                   {
                                     "message": "Cross-file insight.",
                                     "severity": "info"
                                   }
                                 ]
                               }
                               ```
                               """;

        var parsed = SynthesisResponseParser.TryParse(payload, out var summary, out var findings, out _);

        Assert.True(parsed);
        Assert.Equal("Overall summary.", summary);

        var finding = Assert.Single(findings);
        Assert.Equal(CommentSeverity.Info, finding.Severity);
        Assert.Equal(CandidateReviewFinding.CrossCuttingCategory, finding.Category);
        Assert.Equal("finding-cc-unassigned-001", finding.FindingId);

        Assert.NotNull(finding.Evidence);
        var evidence = finding.Evidence!;
        Assert.Equal(EvidenceReference.MissingState, evidence.EvidenceResolutionState);
        Assert.Equal("synthesis_payload", evidence.EvidenceSource);
        Assert.Empty(evidence.SupportingFiles);
    }

    [Fact]
    public void TryParse_WithObjectSummary_UsesRawJsonText()
    {
        const string payload = """
                               {
                                 "summary": { "headline": "Nested summary" },
                                 "cross_cutting_concerns": []
                               }
                               """;

        var parsed = SynthesisResponseParser.TryParse(payload, out var summary, out var findings, out _);

        Assert.True(parsed);
        Assert.Equal("{ \"headline\": \"Nested summary\" }", summary);
        Assert.Empty(findings);
    }

    [Fact]
    public void TryParse_WithBlankConcernMessages_SkipsInvalidConcernsButKeepsSummary()
    {
        const string payload = """
                               {
                                 "summary": "Overall summary.",
                                 "cross_cutting_concerns": [
                                   { "message": "   ", "severity": "warning" },
                                   { "message": "Valid concern.", "severity": "error" }
                                 ]
                               }
                               """;

        var parsed = SynthesisResponseParser.TryParse(payload, out var summary, out var findings, out _);

        Assert.True(parsed);
        Assert.Equal("Overall summary.", summary);
        var finding = Assert.Single(findings);
        Assert.Equal("Valid concern.", finding.Message);
        Assert.Equal(CommentSeverity.Error, finding.Severity);
    }

    [Fact]
    public void StripMarkdownCodeFences_WithoutClosingFence_ReturnsInnerJson()
    {
        const string payload = "```json\n{\"summary\":\"ok\"}";

        var stripped = SynthesisResponseParser.StripMarkdownCodeFences(payload);

        Assert.Equal("{\"summary\":\"ok\"}", stripped);
        Assert.True(SynthesisResponseParser.LooksLikeJsonObject(payload));
    }

    [Fact]
    public void TryParse_WithSummaryFindingIds_ReturnsTheDeclaredIds()
    {
        const string payload = """
                               {
                                 "summary": "Overall summary.",
                                 "summary_finding_ids": ["finding-pf-a-001", "  finding-pf-b-002  ", "", 7],
                                 "cross_cutting_concerns": []
                               }
                               """;

        var parsed = SynthesisResponseParser.TryParse(payload, out _, out _, out var summaryFindingIds);

        Assert.True(parsed);
        Assert.Equal(["finding-pf-a-001", "finding-pf-b-002"], summaryFindingIds);
    }

    [Fact]
    public void TryParse_WithoutSummaryFindingIds_ReturnsAnEmptyList()
    {
        const string payload = """
                               {
                                 "summary": "Overall summary.",
                                 "cross_cutting_concerns": []
                               }
                               """;

        var parsed = SynthesisResponseParser.TryParse(payload, out _, out _, out var summaryFindingIds);

        Assert.True(parsed);
        Assert.Empty(summaryFindingIds);
    }

    [Fact]
    public void TryParse_WithSummaryFindingIdsThatAreNotAnArray_ReturnsAnEmptyList()
    {
        const string payload = """
                               {
                                 "summary": "Overall summary.",
                                 "summary_finding_ids": "finding-pf-a-001",
                                 "cross_cutting_concerns": []
                               }
                               """;

        var parsed = SynthesisResponseParser.TryParse(payload, out _, out _, out var summaryFindingIds);

        Assert.True(parsed);
        Assert.Empty(summaryFindingIds);
    }
}
