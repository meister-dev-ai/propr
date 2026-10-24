// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class QualityFilterSourceIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CombinedFindingRetainsHighestWholeRiskAndWeakestSourceConfidence(bool reverseSources, bool lowerModelRisk)
    {
        var low = Original("Low risk source.") with
        {
            RiskAssessment = Assessment(FindingImpact.Low, FindingLikelihood.Likely, FindingConfidence.Medium),
            OriginPassIndex = 2,
        };
        var critical = Original("Critical source.") with
        {
            RiskAssessment = Assessment(FindingImpact.Critical, FindingLikelihood.Likely, FindingConfidence.High),
            OriginPassIndex = 3,
        };
        ReviewComment[] sources = reverseSources ? [critical, low] : [low, critical];

        var combined = Assert.Single(QualityFilterExecutor.ParseResponse(WithModelMetadata(lowerModelRisk), sources));

        Assert.Equal(critical.RiskAssessment!.Severity, combined.RiskAssessment!.Severity);
        Assert.Equal(critical.RiskAssessment.Impact, combined.RiskAssessment.Impact);
        Assert.Equal(critical.RiskAssessment.Likelihood, combined.RiskAssessment.Likelihood);
        Assert.Equal(critical.RiskAssessment.Source, combined.RiskAssessment.Source);
        Assert.Equal(FindingConfidence.Medium, combined.RiskAssessment.Confidence);
        Assert.Equal(sources[0].OriginPassIndex, combined.OriginPassIndex);
        Assert.Equal(CommentSeverity.Error, combined.Severity);
    }

    [Fact]
    public void CombiningAssessmentAxesCannotCreateRiskHigherThanAnySource()
    {
        var criticalUnlikely = Original("Rare major defect.") with
        {
            RiskAssessment = Assessment(FindingImpact.Critical, FindingLikelihood.Unlikely, FindingConfidence.High),
        };
        var moderateLikely = Original("Frequent bounded defect.") with
        {
            RiskAssessment = Assessment(FindingImpact.Moderate, FindingLikelihood.Likely, FindingConfidence.Certain),
        };

        var combined = Assert.Single(QualityFilterExecutor.ParseResponse(CombinedResponse, [moderateLikely, criticalUnlikely]));

        Assert.Equal(criticalUnlikely.RiskAssessment!.Impact, combined.RiskAssessment!.Impact);
        Assert.Equal(criticalUnlikely.RiskAssessment.ImpactReason, combined.RiskAssessment.ImpactReason);
        Assert.Equal(FindingRiskSeverity.High, combined.RiskAssessment!.Severity);
        Assert.Equal(FindingLikelihood.Unlikely, combined.RiskAssessment.Likelihood);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ConflictingValidatedFixesKeepBothOriginalFindingsAndTheirPatchEvidence(bool sameAnchor, bool modelMetadata)
    {
        var first = Original("First defect.");
        var secondLine = sameAnchor ? 4 : 8;
        var second = Original("Second defect.").WithAnchorAndSeverity("a.cs", secondLine, CommentSeverity.Error) with
        {
            SuggestedFix = new(secondLine, secondLine, "Delete();", "Delete(otherTenant);", "head", "a.cs", true, secondLine),
            OriginPassIndex = 3,
        };
        var originals = new List<ReviewComment> { first, second };
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, WithModelMetadata(modelMetadata))));

        var result = await CreateSut().ApplyAsync(Guid.NewGuid(), originals, new ReviewSystemContext(null, [], null), client, CancellationToken.None);

        Assert.Same(originals, result);
        Assert.Equal([first.Message, second.Message], result.Select(comment => comment.Message));
        Assert.All(result, comment => Assert.True(comment.SuggestedFix!.IsSourceValidated));
        Assert.Equal([2, 3], result.Select(comment => comment.OriginPassIndex));
    }

    [Fact]
    public void ACombinedClaimCannotInheritAnApplicableFixMissingFromOneSource()
    {
        var first = Original("First source claim.");
        var second = Original("Second source claim.") with { SuggestedFix = null };

        Assert.Empty(QualityFilterExecutor.ParseResponse(CombinedResponse, [first, second]));
    }

    [Fact]
    public void IdenticalValidatedFixesRemainApplicableForACombinedClaimAtTheirOriginalAnchor()
    {
        var first = Original("First source claim.");
        var second = Original("Second source claim.");

        var combined = Assert.Single(QualityFilterExecutor.ParseResponse(CombinedResponse, [first, second]));

        Assert.Equal(first.SuggestedFix, combined.SuggestedFix);
        Assert.True(combined.SuggestedFix!.IsSourceValidated);
        Assert.False(combined.SuggestedFix.IsAnchorRemapped);
    }

    [Fact]
    public void AgreedUnscoredAssessmentRetainsIndependentCorrectnessConfidence()
    {
        var assessment = new FindingRiskAssessment(
            FindingImpact.Unknown, FindingLikelihood.Unknown, null, null, "review_model", "finding-risk-v1", FindingConfidence.High, "Supported claim.");
        var first = Original("First source claim.") with { RiskAssessment = assessment };
        var second = Original("Second source claim.") with { RiskAssessment = assessment };

        var combined = Assert.Single(QualityFilterExecutor.ParseResponse(CombinedResponse, [first, second]));

        Assert.Same(assessment, combined.RiskAssessment);
        Assert.Equal(FindingConfidence.High, combined.RiskAssessment!.Confidence);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(true, null, null)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(null, false, false)]
    public void CombinedFindingRecordsRepositoryToolAccessOnlyAsTheSourcesRecordedIt(bool? first, bool? second, bool? expected)
    {
        ReviewComment[] sources =
        [
            Original("First source claim.") with { ProducedWithRepositoryTools = first },
            Original("Second source claim.") with { ProducedWithRepositoryTools = second },
        ];

        var combined = Assert.Single(QualityFilterExecutor.ParseResponse(CombinedResponse, sources));

        Assert.Equal(expected, combined.ProducedWithRepositoryTools);
    }

    private const string CombinedResponse = """
                                            {"comments":[{"source_finding_ids":["finding-filter-002","finding-filter-001"],
                                            "file_path":"a.cs","line_number":4,"severity":"suggestion","message":"Combined source claim."}]}
                                            """;

    private static string WithModelMetadata(bool include) => !include
        ? CombinedResponse
        : CombinedResponse.Replace(
            "\"message\":", """
                            "risk_assessment":{"impact":"low","likelihood":"likely","impact_reason":"Low impact.","likelihood_reason":"Frequent trigger.",
                            "confidence":"certain","confidence_reason":"Model certainty."},
                            "finding_classification":{"types":["logic-error"]},
                            "suggested_fix":{"start_line":4,"end_line":4,"original_text":"Delete();","replacement_text":"Delete(tenant);"},"message":
                            """, StringComparison.Ordinal).Replace("\"severity\":\"suggestion\"", "\"severity\":\"info\"", StringComparison.Ordinal);

    private static FindingRiskAssessment Assessment(FindingImpact impact, FindingLikelihood likelihood, FindingConfidence confidence) =>
        new(
            impact, likelihood, "Supported consequence.", "Supported trigger.", "review_model", "finding-risk-v1", confidence,
            "Supported correctness evidence.");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceIdentityContractRemainsPresentWithPromptOverrides(bool useOverride)
    {
        var context = new ReviewSystemContext(null, [], null)
        {
            PromptOverrides = useOverride
                ? new Dictionary<string, string> { ["QualityFilterSystemPrompt"] = "Custom editor." }
                : new Dictionary<string, string>(),
        };

        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(context);

        Assert.Contains("source_finding_ids", prompt);
        Assert.Contains("Use only IDs from that table", prompt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"malformed\"")]
    [InlineData("{\"types\":[\"unknown\"]}")]
    public async Task RewordedFindingRetainsItsIdentifiedSourcesTypesEvidenceAndDefectSeverity(string classification)
    {
        var first = Original("First source claim.");
        var second = Original("Second source claim.") with
        {
            FindingClassification = first.FindingClassification! with { CoreSlugs = ["concurrency"], CustomTags = [] },
        };
        string? request = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                request = call.Arg<IEnumerable<ChatMessage>>().Last().Text;
                return new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"comments":[{"source_finding_ids":["finding-filter-001"],"file_path":"a.cs","line_number":4,
                                            "severity":"suggestion","message":"An unscoped delete removes other tenants' rows.","finding_classification":
                                            """ + classification + "}]}"));
            });

        var result = await CreateSut().ApplyAsync(Guid.NewGuid(), [first, second], new ReviewSystemContext(null, [], null), client, CancellationToken.None);

        Assert.Contains("finding-filter-001", request);
        Assert.Contains("finding-filter-002", request);
        var rewritten = Assert.Single(result);
        Assert.Equal("An unscoped delete removes other tenants' rows.", rewritten.Message);
        Assert.Same(first.FindingClassification, rewritten.FindingClassification);
        Assert.Same(first.RiskAssessment, rewritten.RiskAssessment);
        Assert.Same(first.SuggestedFix, rewritten.SuggestedFix);
        Assert.Equal(CommentSeverity.Error, rewritten.Severity);
        Assert.Equal(first.OriginModelId, rewritten.OriginModelId);
        Assert.Equal(first.OriginPassIndex, rewritten.OriginPassIndex);
        Assert.Equal(first.ScopeRelation, rewritten.ScopeRelation);
    }

    [Fact]
    public void CombinedFindingRetainsAllIdentifiedSourceTypesEvenWithNewMetadata()
    {
        var first = Original("First source claim.");
        var second = Original("Second source claim.") with
        {
            FindingClassification = first.FindingClassification! with { CoreSlugs = ["concurrency"], CustomTags = [] },
        };
        const string response = """
                                {"comments":[{"source_finding_ids":["finding-filter-001","finding-filter-002"],
                                "file_path":"a.cs","line_number":4,"severity":"suggestion","message":"Combined source claim.",
                                "finding_classification":{"types":["logic-error"],"level":"member","qualifier":"incorrect","confidence":0.99}}]}
                                """;

        var combined = Assert.Single(QualityFilterExecutor.ParseResponse(response, [first, second]));

        Assert.Equal(["logic-error", "concurrency"], combined.FindingClassification!.CoreSlugs);
        Assert.Equal(Assert.Single(first.FindingClassification!.CustomTags), Assert.Single(combined.FindingClassification.CustomTags));
        Assert.Equal(CommentSeverity.Error, combined.Severity);
    }

    [Theory]
    [InlineData("[\"unknown\"]")]
    [InlineData("[\"finding-filter-001\",\"unknown\"]")]
    [InlineData("[1]")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task InvalidSourceReferencesFailOpenWithoutTransferringOtherFindingsMetadata(string ids)
    {
        var original = new List<ReviewComment> { Original("Source claim.") };
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"comments":[{"source_finding_ids":
                                            """ + ids + """
                                                        ,"file_path":"a.cs","line_number":4,"severity":"error","message":"Reworded claim."}]}
                                                        """)));

        var result = await CreateSut().ApplyAsync(Guid.NewGuid(), original, new ReviewSystemContext(null, [], null), client, CancellationToken.None);

        Assert.Same(original, result);
    }

    [Fact]
    public void AmbiguousLegacyMatchCannotChooseAnotherFindingsTypes()
    {
        var first = Original("Same claim.");
        var second = first with { FindingClassification = first.FindingClassification! with { CoreSlugs = ["concurrency"], CustomTags = [] } };
        var response = JsonSerializer.Serialize(
            new
            {
                comments = new[] { new { file_path = "a.cs", line_number = 4, severity = "error", message = "Same claim." } },
            });

        Assert.Empty(QualityFilterExecutor.ParseResponse(response, [first, second]));
    }

    [Fact]
    public void RewordingAtADifferentAnchorRetainsEvidenceButInvalidatesApplyingTheOriginalFix()
    {
        var original = Original("Source claim.");
        const string response = """
                                {"comments":[{"source_finding_ids":["finding-filter-001"],"file_path":"a.cs","line_number":5,
                                "severity":"error","message":"Reworded source claim."}]}
                                """;

        var rewritten = Assert.Single(QualityFilterExecutor.ParseResponse(response, [original]));

        Assert.Same(original.FindingClassification, rewritten.FindingClassification);
        Assert.Same(original.RiskAssessment, rewritten.RiskAssessment);
        Assert.Equal(original.SuggestedFix!.ReplacementText, rewritten.SuggestedFix!.ReplacementText);
        Assert.False(rewritten.SuggestedFix.IsSourceValidated);
        Assert.True(rewritten.SuggestedFix.IsAnchorRemapped);
    }

    private static ReviewComment Original(string message)
    {
        return new("a.cs", 4, CommentSeverity.Error, message)
        {
            FindingClassification = new(
                ["logic-error"], [new(Guid.NewGuid(), "tenant-boundary", "Tenant boundary")],
                CodeInsightFindingLevel.Member, CodeInsightFindingQualifier.Incorrect, 0.96, 1, FindingTypeClassification.ReviewSource),
            RiskAssessment = new(FindingImpact.Critical, FindingLikelihood.Likely, "Tenant data lost.", "Every delete.", "review_model", "finding-risk-v1"),
            SuggestedFix = new(4, 4, "Delete();", "Delete(tenant);", "head", "a.cs", true, 4),
            OriginModelId = "model",
            OriginPassIndex = 2,
            ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
        };
    }

    private static QualityFilterExecutor CreateSut() => new(new AiReviewOptions(), NullLogger<FileByFileReviewOrchestrator>.Instance);
}
