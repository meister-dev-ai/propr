// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Tests.AI;

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

        var parsed = SynthesisResponseParser.TryParse(payload, out var summary, out var findings);

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

        var parsed = SynthesisResponseParser.TryParse(payload, out var summary, out var findings);

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

        var parsed = SynthesisResponseParser.TryParse(payload, out var summary, out var findings);

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
}
