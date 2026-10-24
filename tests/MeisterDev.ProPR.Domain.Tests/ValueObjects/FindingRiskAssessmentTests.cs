// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

public sealed class FindingRiskAssessmentTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("\" \"")]
    public void MalformedIndependentSource_DoesNotBorrowTheRiskProducer(string source)
    {
        var assessment = JsonSerializer.Deserialize<FindingRiskAssessment>(
            """
            {"Impact":3,"Likelihood":1,"ImpactReason":"Data loss.","LikelihoodReason":"Exceptional input.",
            "Source":"review_model","RubricVersion":"finding-risk-v1","Confidence":4,"ConfidenceReason":"Evidence.","ConfidenceSource":
            """ + source + "}");

        Assert.Equal(FindingConfidence.Unknown, assessment!.Confidence);
        Assert.Equal(FindingRiskSeverity.Medium, assessment.Severity);
        Assert.Equal("review_model", assessment.Source);
    }

    [Fact]
    public void Assessment_RoundTripsIndependentConfidenceSource()
    {
        var assessment = JsonSerializer.Deserialize<FindingRiskAssessment>(
            """
            {"Impact":3,"Likelihood":1,"ImpactReason":"Data loss.","LikelihoodReason":"Exceptional input.",
            "Source":"review_model","RubricVersion":"finding-risk-v1","Confidence":3,
            "ConfidenceReason":"Independent source witness.","ConfidenceSource":"evidence_verification"}
            """);

        var restored = JsonSerializer.SerializeToElement(assessment);
        Assert.Equal("review_model", restored.GetProperty("Source").GetString());
        Assert.Equal("evidence_verification", restored.GetProperty("ConfidenceSource").GetString());
        Assert.Equal(3, restored.GetProperty("Confidence").GetInt32());
    }

    [Fact]
    public void ConfidenceRemainsIndependentWhenOccurrenceIsNotApplicable()
    {
        var risk = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.NotApplicable, "Maintenance cost.", null, "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete call graph proves this dependency is unused.");
        Assert.Equal(FindingConfidence.Certain, risk.Confidence);
        Assert.Equal(FindingRiskSeverity.Unknown, risk.Severity);
    }

    [Theory]
    [InlineData(null, "finding-risk-v1", 20)]
    [InlineData("review_model", "unsupported", 20)]
    [InlineData("review_model", "finding-risk-v1", 1025)]
    public void ConfidenceRequiresSupportedProvenanceAndBoundedEvidence(string? source, string version, int length)
    {
        var risk = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Malformed input.", source,
            version, FindingConfidence.Certain, new string('e', length));
        Assert.Equal(FindingConfidence.Unknown, risk.Confidence);
        Assert.True(risk.ConfidenceReason is null || risk.ConfidenceReason.Length <= FindingRiskAssessment.MaxReasonLength);
    }

    [Theory]
    [InlineData("low", 1)]
    [InlineData("medium", 2)]
    [InlineData("high", 3)]
    [InlineData("certain", 4)]
    public void Assessment_RetainsConfidenceEvidenceWithoutChangingRisk(string confidence, int expected)
    {
        var assessment = JsonSerializer.Deserialize<FindingRiskAssessment>(
            """{"Impact":3,"Likelihood":1,"ImpactReason":"Data loss.","LikelihoodReason":"Exceptional input.","Source":"review_model","RubricVersion":"finding-risk-v1","confidence":""" +
            "\"" + confidence + "\"" + """, "confidence_reason":"Direct reproduction."}""");
        var serialized = JsonSerializer.SerializeToElement(assessment);

        Assert.Equal(expected, serialized.GetProperty("Confidence").GetInt32());
        Assert.Equal("Direct reproduction.", serialized.GetProperty("ConfidenceReason").GetString());
        Assert.Equal(FindingRiskSeverity.Medium, assessment!.Severity);
    }

    [Theory]
    [InlineData("99", "Evidence.")]
    [InlineData("true", "Evidence.")]
    [InlineData("{}", "Evidence.")]
    [InlineData("\"4\"", "Evidence.")]
    [InlineData("\"certain\"", " ")]
    public void Assessment_InvalidConfidenceRemainsUnassessedAndRetainsRisk(string confidence, string reason)
    {
        var assessment = JsonSerializer.Deserialize<FindingRiskAssessment>(
            """{"Impact":3,"Likelihood":1,"ImpactReason":"Data loss.","LikelihoodReason":"Exceptional input.","Source":"review_model","RubricVersion":"finding-risk-v1","confidence":""" +
            confidence + ",\"confidence_reason\":" + JsonSerializer.Serialize(reason) + "}");

        Assert.Equal(0, JsonSerializer.SerializeToElement(assessment).GetProperty("Confidence").GetInt32());
        Assert.Equal(FindingRiskSeverity.Medium, assessment!.Severity);
    }

    [Theory]
    [InlineData(FindingImpact.Low, FindingLikelihood.Unlikely, FindingRiskSeverity.Low)]
    [InlineData(FindingImpact.Low, FindingLikelihood.Possible, FindingRiskSeverity.Low)]
    [InlineData(FindingImpact.Low, FindingLikelihood.Likely, FindingRiskSeverity.Low)]
    [InlineData(FindingImpact.Moderate, FindingLikelihood.Unlikely, FindingRiskSeverity.Low)]
    [InlineData(FindingImpact.Moderate, FindingLikelihood.Possible, FindingRiskSeverity.Medium)]
    [InlineData(FindingImpact.Moderate, FindingLikelihood.Likely, FindingRiskSeverity.Medium)]
    [InlineData(FindingImpact.High, FindingLikelihood.Unlikely, FindingRiskSeverity.Medium)]
    [InlineData(FindingImpact.High, FindingLikelihood.Possible, FindingRiskSeverity.High)]
    [InlineData(FindingImpact.High, FindingLikelihood.Likely, FindingRiskSeverity.High)]
    [InlineData(FindingImpact.Critical, FindingLikelihood.Unlikely, FindingRiskSeverity.High)]
    [InlineData(FindingImpact.Critical, FindingLikelihood.Possible, FindingRiskSeverity.High)]
    [InlineData(FindingImpact.Critical, FindingLikelihood.Likely, FindingRiskSeverity.Critical)]
    public void Severity_UsesVersionedImpactLikelihoodMatrix(FindingImpact impact, FindingLikelihood likelihood, FindingRiskSeverity expected)
    {
        Assert.Equal(expected, Assess(impact, likelihood).Severity);
    }

    [Fact]
    public void Assessment_IsIndependentOfLegacyClassificationAndSurvivesPublicationCopyAndJson()
    {
        var assessment = Assess(FindingImpact.Moderate, FindingLikelihood.Unlikely);
        var comment = new ReviewComment("src/Work.cs", 7, CommentSeverity.Error, "A limited defect.") { RiskAssessment = assessment };
        var restored = JsonSerializer.Deserialize<ReviewComment>(JsonSerializer.Serialize(comment));

        Assert.Equal(FindingRiskSeverity.Low, restored!.RiskAssessment!.Severity);
        Assert.Equal(CommentSeverity.Error, restored.Severity);
        Assert.Equal(assessment, restored.AsPullRequestLevel("A limited defect.").RiskAssessment);
    }

    [Theory]
    [InlineData(FindingImpact.Unknown, FindingLikelihood.Likely)]
    [InlineData(FindingImpact.Critical, FindingLikelihood.Unknown)]
    [InlineData(FindingImpact.High, FindingLikelihood.NotApplicable)]
    [InlineData((FindingImpact)99, FindingLikelihood.Likely)]
    [InlineData(FindingImpact.Critical, (FindingLikelihood)99)]
    public void Severity_UnavailableAxesRemainUnknown(FindingImpact impact, FindingLikelihood likelihood)
    {
        Assert.Equal(FindingRiskSeverity.Unknown, Assess(impact, likelihood).Severity);
    }

    [Theory]
    [InlineData(null, "Scenario occurs in routine use.", "finding-risk-v1")]
    [InlineData(" ", "Scenario occurs in routine use.", "finding-risk-v1")]
    [InlineData("Deletes tenant data.", null, "finding-risk-v1")]
    [InlineData("Deletes tenant data.", "Scenario occurs in routine use.", "future-version")]
    public void Severity_IncompleteOrUnsupportedAssessmentRemainsUnknown(string? impactReason, string? likelihoodReason, string? version)
    {
        var assessment = new FindingRiskAssessment(FindingImpact.Critical, FindingLikelihood.Likely, impactReason, likelihoodReason, "review_model", version);
        Assert.Equal(FindingRiskSeverity.Unknown, assessment.Severity);
        Assert.Equal(FindingImpact.Critical, assessment.Impact);
    }

    [Fact]
    public void Assessment_LegacyJsonKeepsAssessmentAbsent()
    {
        var comment = JsonSerializer.Deserialize<ReviewComment>("""{"FilePath":null,"LineNumber":null,"Severity":2,"Message":"Historical finding."}""");
        Assert.Null(comment!.RiskAssessment);
    }

    [Fact]
    public void Severity_ExcessiveRationaleIsUnknownAndBounded()
    {
        var assessment = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Likely, new string('a', 10000), "Occurs in normal use.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion);
        Assert.Equal(FindingRiskSeverity.Unknown, assessment.Severity);
        Assert.True(assessment.ImpactReason is null || assessment.ImpactReason.Length <= FindingRiskAssessment.MaxReasonLength);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{\"Impact\":\"nonsense\",\"Likelihood\":{}}")]
    public void Assessment_MalformedTransportDoesNotInvalidateFinding(string payload)
    {
        var comment = JsonSerializer.Deserialize<ReviewComment>("{\"Severity\":2,\"Message\":\"Keep finding.\",\"RiskAssessment\":" + payload + "}");
        Assert.Equal("Keep finding.", comment!.Message);
        Assert.True(comment.RiskAssessment is null || comment.RiskAssessment.Severity == FindingRiskSeverity.Unknown);
    }

    private static FindingRiskAssessment Assess(FindingImpact impact, FindingLikelihood likelihood) =>
        new(impact, likelihood, "Deletes tenant data.", "Scenario occurs in routine use.", "review_model", FindingRiskAssessment.CurrentRubricVersion);
}
