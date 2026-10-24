// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class FindingTypePipelineTests
{
    private static readonly Guid CustomId = Guid.NewGuid();
    private static readonly FindingTypeVocabulary Vocabulary = new(1, [new(CustomId, "tenant-boundary", "Tenant boundary", "Cross-tenant access.")]);

    private const string Finding = """
                                   {"id":"candidate-1","message":"An unscoped delete removes another tenant's rows.","severity":"error","file_path":"a.cs","line_number":4,
                                   "finding_classification":{"types":["logic-error","tenant-boundary"],"level":"member","qualifier":"incorrect","confidence":0.96}}
                                   """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothReviewCoresProduceClassificationsFromTheReviewCall(bool tools)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"summary\":\"Review.\",\"loop_complete\":true,\"comments\":[" + Finding + "]}")));
        var context = new ReviewSystemContext(null, [], null) { FindingTypeVocabulary = Vocabulary };
        var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxIterations = 1 });
        var pr = new PullRequest("https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Review", null, "feature", "main", []);
        var result = tools
            ? await new ToolAwareAiReviewCore(client, options, NullLogger<ToolAwareAiReviewCore>.Instance).ReviewAsync(pr, context)
            : await new AgentAiReviewCore(client, options).ReviewAsync(pr, context);
        AssertTypes(Assert.Single(result.Comments).FindingClassification);
        await client.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void InvestigationAndSynthesisParsersCarryCapturedCustomTypesIntoCandidates()
    {
        var payload = "{\"summary\":\"Review.\",\"candidate_findings\":[" + Finding + "]}";
        Assert.True(
            AgenticFileArtifactParser.TryParseInvestigationResult(
                payload,
                new("task", "concern", "trigger", "deletion", ["a.cs"], [], 1), out var file, Vocabulary));
        AssertTypes(Assert.Single(file!.CandidateFindings).ToCandidateReviewFinding(new("per_file_comment", "investigation")).FindingClassification);
        Assert.True(
            PrWideArtifactParser.TryParseInvestigationResult(
                payload,
                new("task", "concern", "deletion", ["a.cs"], [], 1), [], out var wide, Vocabulary));
        AssertTypes(Assert.Single(wide!.CandidateFindings).ToCandidateReviewFinding(new("pr_wide_pass", "investigation")).FindingClassification);
        Assert.True(PrWideArtifactParser.TryParseSynthesizedCandidates(payload, out var synthesized, Vocabulary));
        AssertTypes(Assert.Single(synthesized).FindingClassification);
        Assert.True(
            SynthesisResponseParser.TryParse(
                "{\"summary\":\"Review.\",\"cross_cutting_concerns\":[" + Finding + "]}",
                out _, out var concerns, out _, Vocabulary));
        AssertTypes(Assert.Single(CandidateFindingFactory.AssignSynthesisFindingIds(concerns)).FindingClassification);
    }

    [Fact]
    public void MissingFilterMetadataKeepsSourceTypesAndDefectClassification()
    {
        var original = Assert.Single(QualityFilterExecutor.ParseResponse("{\"comments\":[" + Finding + "]}", vocabulary: Vocabulary));
        var payload =
            "{\"comments\":[{\"file_path\":\"a.cs\",\"line_number\":4,\"severity\":\"suggestion\",\"message\":\"An unscoped delete removes another tenant's rows.\"}]}";
        var filtered = Assert.Single(QualityFilterExecutor.ParseResponse(payload, [original], Vocabulary));
        Assert.Equal(CommentSeverity.Error, filtered.Severity);
        AssertTypes(filtered.FindingClassification);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SynthesisRetainsDeclaredSourceTypesEvenWhenItSuppliesItsOwnMetadata(bool suppliedMetadata, bool sameWording)
    {
        var types = new FindingTypeClassification(
            ["logic-error"], [new(CustomId, "tenant-boundary", "Tenant boundary")], CodeInsightFindingLevel.Member,
            CodeInsightFindingQualifier.Incorrect, 0.96, 1, FindingTypeClassification.ReviewSource);
        var first = new CandidateReviewFinding(
            "first", new("per_file_comment", "review"), CommentSeverity.Error, "First claim.",
            CandidateReviewFinding.PerFileCommentCategory, "a.cs", 1) { FindingClassification = types };
        var second = new CandidateReviewFinding(
            "second", first.Provenance, CommentSeverity.Error, "Second claim.",
            CandidateReviewFinding.PerFileCommentCategory, "b.cs", 2)
        {
            FindingClassification = types with { CoreSlugs = ["concurrency"], Qualifier = CodeInsightFindingQualifier.Missing },
        };
        var combined = new CandidateReviewFinding(
            "combined", new("synthesis", "synthesis"), CommentSeverity.Error, sameWording ? first.Message : "Combined claim.",
            CandidateReviewFinding.CrossCuttingCategory, sameWording ? first.FilePath : null, sameWording ? first.LineNumber : null,
            new(["first", "second"], ["a.cs", "b.cs"], "resolved", "synthesis"))
        {
            FindingClassification = suppliedMetadata ? types with { CoreSlugs = ["logic-error"], CustomTags = [] } : null,
        };
        var retained = Assert.Single(CandidateFindingFactory.RetainSourceClassifications([combined], [first, second]));
        Assert.Equal(["logic-error", "concurrency"], retained.FindingClassification!.CoreSlugs);
        Assert.Equal(CustomId, Assert.Single(retained.FindingClassification.CustomTags).Id);
        Assert.Null(retained.FindingClassification.Qualifier);
        Assert.False(retained.FindingClassification.IsComplete);
        Assert.Equal(CommentSeverity.Error, retained.Severity);
    }

    [Fact]
    public void UnchangedSynthesisClaimKeepsItsOriginalClassificationWhenModelSuppliesDifferentTypes()
    {
        var types = new FindingTypeClassification(
            ["logic-error"], [new(CustomId, "tenant-boundary", "Tenant boundary")], CodeInsightFindingLevel.Member,
            CodeInsightFindingQualifier.Incorrect, 0.96, 1, FindingTypeClassification.ReviewSource);
        var original = new CandidateReviewFinding(
            "first", new("per_file_comment", "review"), CommentSeverity.Error, "Same claim.",
            CandidateReviewFinding.PerFileCommentCategory, "a.cs", 1) { FindingClassification = types };
        var synthesized = original with { FindingClassification = types with { CoreSlugs = ["security"], CustomTags = [] } };

        var retained = Assert.Single(CandidateFindingFactory.RetainSourceClassifications([synthesized], [original]));

        Assert.Same(types, retained.FindingClassification);
    }

    [Fact]
    public void RewrittenNewSynthesisClaimCanKeepItsOwnClassification()
    {
        var original = new CandidateReviewFinding(
            "first", new("per_file_comment", "review"), CommentSeverity.Error, "Original claim.",
            CandidateReviewFinding.PerFileCommentCategory, "a.cs", 1)
        {
            FindingClassification = new(["logic-error"], [], null, null, null, 1, FindingTypeClassification.ReviewSource),
        };
        var newClaim = new CandidateReviewFinding(
            "second", original.Provenance, CommentSeverity.Error, "A different supported claim.", original.Category, original.FilePath, original.LineNumber)
        {
            FindingClassification = new(["concurrency"], [], null, null, null, 1, FindingTypeClassification.ReviewSource),
        };

        var retained = Assert.Single(CandidateFindingFactory.RetainSourceClassifications([newClaim], [original]));

        Assert.Same(newClaim.FindingClassification, retained.FindingClassification);
    }

    [Theory]
    [InlineData("ado", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("ado", false, FindingBadgeStyle.Plain)]
    [InlineData("ado", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("ado", true, FindingBadgeStyle.Plain)]
    [InlineData("github", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("github", false, FindingBadgeStyle.Plain)]
    [InlineData("github", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("github", true, FindingBadgeStyle.Plain)]
    [InlineData("gitlab", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("gitlab", false, FindingBadgeStyle.Plain)]
    [InlineData("gitlab", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("gitlab", true, FindingBadgeStyle.Plain)]
    [InlineData("forgejo", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("forgejo", false, FindingBadgeStyle.Plain)]
    [InlineData("forgejo", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("forgejo", true, FindingBadgeStyle.Plain)]
    public void EveryProviderDisplaysTypesAndRestoresRawClaim(string provider, bool assessed, FindingBadgeStyle style)
    {
        var finding = Assert.Single(QualityFilterExecutor.ParseResponse("{\"comments\":[" + Finding + "]}", vocabulary: Vocabulary));
        if (assessed)
        {
            finding = finding with
            {
                RiskAssessment = new(FindingImpact.Critical, FindingLikelihood.Likely, "Tenant data lost.", "Every delete", "review_model", "finding-risk-v1")
            };
        }

        var body = provider switch
        {
            "ado" => AdoFindingCommentRenderer.Format(finding, badgeStyle: style),
            "github" => GitHubFindingCommentRenderer.Format(finding, badgeStyle: style),
            "gitlab" => GitLabFindingCommentRenderer.Format(finding, badgeStyle: style),
            _ => ForgejoFindingCommentRenderer.Format(finding, badgeStyle: style),
        };
        Assert.Contains("Type: Logic error", body);
        Assert.Contains("Type: Tenant boundary", body);
        Assert.Equal(style == FindingBadgeStyle.ShieldsIo, body.Contains("https://img.shields.io", StringComparison.Ordinal));
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindingProducersReceiveVocabularyEvenWithPromptOverrides(bool overrides)
    {
        var context = new ReviewSystemContext(null, [], null)
        {
            FindingTypeVocabulary = Vocabulary,
            PromptOverrides = overrides
                ? new Dictionary<string, string>
                {
                    ["SystemPrompt"] = "Custom reviewer.", ["AgenticFileInvestigationSystemPrompt"] = "Custom file.",
                    ["PrWideInvestigationSystemPrompt"] = "Custom investigation.", ["PrWideSynthesisSystemPrompt"] = "Custom synthesis.",
                    ["SynthesisSystemPrompt"] = "Custom summary.", ["QualityFilterSystemPrompt"] = "Custom filter.",
                    ["MemoryReconsiderationSystemPrompt"] = "Custom memory.",
                }
                : new Dictionary<string, string>(),
        };
        var prompts = new[]
        {
            ReviewPrompts.BuildSystemPrompt(context), ReviewPrompts.BuildAgenticFileInvestigationSystemPrompt(context),
            ReviewPrompts.BuildPrWideInvestigationSystemPrompt(context), ReviewPrompts.BuildPrWideSynthesisSystemPrompt(context),
            ReviewPrompts.BuildSynthesisSystemPrompt(context, true), ReviewPrompts.BuildQualityFilterSystemPrompt(context),
            ReviewPrompts.BuildMemoryReconsiderationSystemPrompt(context),
        };
        Assert.All(
            prompts, prompt =>
            {
                Assert.Contains("finding_classification", prompt);
                Assert.Contains("logic-error", prompt);
                Assert.Contains("tenant-boundary", prompt);
                Assert.Contains("does not limit the issues you may report", prompt);
            });
    }

    private static void AssertTypes(FindingTypeClassification? classification)
    {
        Assert.NotNull(classification);
        Assert.True(classification.IsComplete);
        Assert.Equal(["logic-error"], classification.CoreSlugs);
        Assert.Equal(CustomId, Assert.Single(classification.CustomTags).Id);
        Assert.Equal(0.96, classification.Confidence);
    }
}
