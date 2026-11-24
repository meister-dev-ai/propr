// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution.Services;

/// <summary>Covers the provenance that the server records on an assessment the review model returns.</summary>
public sealed class FindingRiskAssessmentParserTests
{
    private const string Finding = """
                                   {"message":"An import loses the stored settings.","risk_assessment":{"impact":"moderate","likelihood":"unlikely",
                                   "impact_reason":"One import fails.","likelihood_reason":"Only a hand-built request reaches the branch.",
                                   "confidence":"medium","confidence_reason":"The parser is visible; the caller is not."MODEL_PROVENANCE}}
                                   """;

    [Theory]
    [InlineData("")]
    [InlineData(",\"rubric_version\":\"finding-risk-v1\",\"source\":\"model\"")]
    public void ParseFinding_RecordsTheSecondRubricVersionWhateverTheModelReturns(string modelProvenance)
    {
        using var document = JsonDocument.Parse(Finding.Replace("MODEL_PROVENANCE", modelProvenance, StringComparison.Ordinal));

        var assessment = FindingRiskAssessmentParser.ParseFinding(document.RootElement)!;

        Assert.Equal("finding-risk-v2", assessment.RubricVersion);
        Assert.Equal("review_model", assessment.Source);
        Assert.Equal(FindingRiskSeverity.Low, assessment.Severity);
        Assert.Equal(FindingConfidence.Medium, assessment.Confidence);
    }

    [Fact]
    public void Parse_RecordsTheSecondRubricVersionOnAnUnreadableAssessment()
    {
        using var document = JsonDocument.Parse("42");

        var assessment = FindingRiskAssessmentParser.Parse(document.RootElement)!;

        Assert.Equal("finding-risk-v2", assessment.RubricVersion);
        Assert.Equal(FindingRiskSeverity.Unknown, assessment.Severity);
    }
}
