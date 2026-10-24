// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class FindingRiskPipelineTests
{
    private const string AssessedFinding = """
                                           {"id":"candidate-1","message":"Deletes unrelated tenant records.","severity":"error",
                                            "file_path":"src/Work.cs","line_number":7,
                                            "risk_assessment":{"impact":"critical","likelihood":"likely","impact_reason":"Deletes unrelated tenant records.",
                                            "likelihood_reason":"Every tenant deletion uses the unscoped query.","confidence":"certain","confidence_reason":"The unscoped delete deterministically includes other tenants.","severity":"low","source":"forged","rubric_version":"forged"}}
                                           """;

    [Fact]
    public void QualityFilter_ParsesIndependentAssessmentAndOwnsDerivedSeverityAndProvenance()
    {
        var comment = Assert.Single(QualityFilterExecutor.ParseResponse("{\"comments\":[" + AssessedFinding + "]}"));
        Assert.Equal(CommentSeverity.Error, comment.Severity);
        AssertRisk(comment.RiskAssessment);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{\"impact\":\"unsupported\",\"likelihood\":\"likely\"}")]
    [InlineData("{\"impact\":\"critical\",\"likelihood\":{},\"impact_reason\":false}")]
    public void QualityFilter_MalformedAssessmentNeverDiscardsFinding(string assessment)
    {
        var comment = Assert.Single(
            QualityFilterExecutor.ParseResponse(
                "{\"comments\":[{\"severity\":\"error\",\"message\":\"Keep this finding.\",\"risk_assessment\":" + assessment + "}]}"));
        Assert.Equal("Keep this finding.", comment.Message);
        Assert.True(comment.RiskAssessment is null || comment.RiskAssessment.Severity == FindingRiskSeverity.Unknown);
    }

    [Fact]
    public void Synthesis_AssessmentSurvivesCandidateIdAssignment()
    {
        Assert.True(
            SynthesisResponseParser.TryParse("{\"summary\":\"Review.\",\"cross_cutting_concerns\":[" + AssessedFinding + "]}", out _, out var findings, out _));
        AssertRisk(Assert.Single(CandidateFindingFactory.AssignSynthesisFindingIds(findings)).RiskAssessment);
    }

    [Fact]
    public void PrWide_AssessmentSurvivesSynthesisAndCandidateConversion()
    {
        Assert.True(PrWideArtifactParser.TryParseSynthesizedCandidates("{\"candidate_findings\":[" + AssessedFinding + "]}", out var findings));
        AssertRisk(Assert.Single(findings).ToCandidateReviewFinding(new CandidateFindingProvenance("pr_wide_pass", "synthesis")).RiskAssessment);
    }

    [Fact]
    public void AgenticInvestigation_AssessmentSurvivesCandidateConversion()
    {
        var task = new AgenticFileInvestigationTask("task-1", "concern", "trigger", "deletion", ["src/Work.cs"], [], 1);
        Assert.True(AgenticFileArtifactParser.TryParseInvestigationResult("{\"candidate_findings\":[" + AssessedFinding + "]}", task, out var result));
        AssertRisk(
            Assert.Single(result!.CandidateFindings).ToCandidateReviewFinding(new CandidateFindingProvenance("per_file_comment", "investigation"))
                .RiskAssessment);
    }

    [Fact]
    public void ConfidenceFloor_DoesNotChangeImpactLikelihoodOrRawMessage()
    {
        var risk = new FindingRiskAssessment(
            FindingImpact.Critical, FindingLikelihood.Likely, "Deletes tenant data.", "Runs for every delete.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion);
        var comment = new ReviewComment("src/Work.cs", 7, CommentSeverity.Error, "Original finding.") { RiskAssessment = risk };
        var result = FileByFileReviewOrchestrator.ApplyConfidenceFloor(
            new ReviewResult("Review.", [comment]), 0, new MeisterDev.ProPR.Application.Options.AiReviewOptions());
        var adjusted = Assert.Single(result.Comments);
        Assert.Equal(CommentSeverity.Error, adjusted.Severity);
        Assert.Equal(risk, adjusted.RiskAssessment);
        Assert.Equal(comment.Message, adjusted.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewCores_ParseAssessmentWithoutAdditionalModelCalls(bool toolAware)
    {
        var client = Substitute.For<IChatClient>();
        var payload = "{\"comments\":[" + AssessedFinding + "],\"loop_complete\":true,\"confidence_evaluations\":[]}";
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, payload)));
        var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxIterations = 1 });
        var pr = new PullRequest("https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Review", null, "branch", "main", []);
        var context = new ReviewSystemContext(null, [], null);
        var result = toolAware
            ? await new ToolAwareAiReviewCore(client, options, NullLogger<ToolAwareAiReviewCore>.Instance).ReviewAsync(pr, context)
            : await new AgentAiReviewCore(client, options).ReviewAsync(pr, context);

        AssertRisk(Assert.Single(result.Comments).RiskAssessment);
        await client.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CandidateMaterialization_PreservesRiskForPublishedAndWithheldDecisions()
    {
        var risk = new FindingRiskAssessment(
            FindingImpact.Critical, FindingLikelihood.Likely, "Deletes tenant data.", "Every delete uses this query.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion);
        var candidate = new CandidateReviewFinding(
            "candidate-1", new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"), CommentSeverity.Error,
            "Unscoped delete.", CandidateReviewFinding.PerFileCommentCategory, "src/Work.cs", 7) { RiskAssessment = risk };
        var publish = new FinalGateDecision(candidate.FindingId, FinalGateDecision.PublishDisposition, ["supported"], "rules", [], null, null);
        var withhold = new FinalGateDecision(
            candidate.FindingId, FinalGateDecision.SummaryOnlyDisposition, ["verification_withheld"], "rules", [], null, "Retained as summary evidence.");

        var published = Assert.Single(ReviewSynthesisExecutor.MaterializePublishedComments([candidate], [publish]));
        Assert.Equal(CommentSeverity.Error, published.Severity);
        var publishedRisk = Assert.IsType<FindingRiskAssessment>(published.RiskAssessment);
        Assert.Equal(risk.Impact, publishedRisk.Impact);
        Assert.Equal(risk.Likelihood, publishedRisk.Likelihood);
        Assert.Equal(risk.ImpactReason, publishedRisk.ImpactReason);
        Assert.Equal(risk.LikelihoodReason, publishedRisk.LikelihoodReason);
        Assert.Equal(risk.Source, publishedRisk.Source);
        Assert.Equal(risk.RubricVersion, publishedRisk.RubricVersion);
        Assert.Equal(FindingConfidence.Unknown, publishedRisk.Confidence);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, publishedRisk.ConfidenceSource);
        var withheld = Assert.Single(WithheldReviewFindingMapper.Materialize([candidate], [withhold])).Comment;
        Assert.Equal(CommentSeverity.Error, withheld.Severity);
        Assert.Equal(risk, withheld.RiskAssessment);
    }

    [Fact]
    public void RenderedPrompts_RequireIndependentAssessmentAndCarryExistingEvidence()
    {
        var risk = new FindingRiskAssessment(
            FindingImpact.Critical, FindingLikelihood.Likely, "Deletes tenant data.", "Every delete uses this query.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion);
        var comment = new ReviewComment("src/Work.cs", 7, CommentSeverity.Error, "Unscoped delete.") { RiskAssessment = risk };
        Assert.Contains("independently", ReviewPrompts.BuildGlobalSystemPrompt(null));
        Assert.Contains("risk_assessment", ReviewPrompts.BuildQualityFilterSystemPrompt(null));
        Assert.Contains("Deletes tenant data.", ReviewPrompts.BuildQualityFilterUserMessage([comment]));
        Assert.Contains("Every delete uses this query.", ReviewPrompts.BuildSynthesisUserMessage([], "Review", null, [comment]));
        Assert.Contains("risk_assessment", ReviewPrompts.BuildPrWideInvestigationSystemPrompt(null));
        Assert.Contains("risk_assessment", ReviewPrompts.BuildAgenticFileInvestigationSystemPrompt(null));
    }

    private static void AssertRisk(FindingRiskAssessment? risk)
    {
        Assert.NotNull(risk);
        Assert.Equal(FindingImpact.Critical, risk.Impact);
        Assert.Equal(FindingLikelihood.Likely, risk.Likelihood);
        Assert.Equal(FindingConfidence.Certain, risk.Confidence);
        Assert.Equal("The unscoped delete deterministically includes other tenants.", risk.ConfidenceReason);
        Assert.Equal(FindingRiskSeverity.Critical, risk.Severity);
        Assert.Equal("review_model", risk.Source);
        Assert.Equal(FindingRiskAssessment.CurrentRubricVersion, risk.RubricVersion);
    }
}
