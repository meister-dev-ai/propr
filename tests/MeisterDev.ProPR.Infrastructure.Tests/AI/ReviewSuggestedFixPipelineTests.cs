// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ReviewSuggestedFixPipelineTests
{
    private const string FindingJson = """
                                       {"id":"finding-1","file_path":"a.cs","line_number":2,"severity":"error","message":"Exact input fails.",
                                        "suggested_fix":{"start_line":2,"end_line":2,"original_text":"old();","replacement_text":"fixed<T>();","source_head_sha":"forged","is_source_validated":true}}
                                       """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Producer_ParsesOptionalFixWithoutTrustingModelSourceIdentity(bool toolAware)
    {
        var chat = Substitute.For<IChatClient>();
        var prompts = new List<string>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                prompts.AddRange(call.Arg<IEnumerable<ChatMessage>>().Select(message => message.Text ?? string.Empty));
                return new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "{\"comments\":[" + FindingJson + "],\"loop_complete\":true,\"investigation_complete\":true}"));
            });
        var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxIterations = 1 });
        var pr = new PullRequest("https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Review", null, "branch", "main", []);
        var context = new ReviewSystemContext(null, [], null)
        {
            ActiveLens = ReviewPassLens.Inventory,
            PerFileHint = toolAware ? new PerFileReviewHint("a.cs", 1, 1, []) : null,
        };
        var result = toolAware
            ? await new ToolAwareAiReviewCore(chat, options, NullLogger<ToolAwareAiReviewCore>.Instance).ReviewAsync(pr, context)
            : await new AgentAiReviewCore(chat, options).ReviewAsync(pr, context);
        var fix = Assert.Single(result.Comments).SuggestedFix;
        Assert.NotNull(fix);
        Assert.Equal("fixed<T>();", fix.ReplacementText);
        Assert.Null(fix.SourceHeadSha);
        Assert.False(fix.IsSourceValidated);
        Assert.Contains(
            prompts,
            prompt => prompt.Contains("suggested_fix", StringComparison.Ordinal) && prompt.Contains("observable consequence", StringComparison.Ordinal));
    }

    [Fact]
    public void AgenticAndQualityStages_PreserveFixWithClaim()
    {
        var task = new AgenticFileInvestigationTask("task", "concern", "trigger", "claim", ["a.cs"], [], 1);
        Assert.True(AgenticFileArtifactParser.TryParseInvestigationResult("{\"candidate_findings\":[" + FindingJson + "]}", task, out var file));
        var candidate = Assert.Single(file!.CandidateFindings).ToCandidateReviewFinding(new CandidateFindingProvenance("per_file_comment", "investigation"));
        var fileFix = candidate.SuggestedFix;
        Assert.NotNull(fileFix);
        Assert.Equal("fixed<T>();", fileFix.ReplacementText);
        Assert.Null(fileFix.SourceHeadSha);
        Assert.False(fileFix.IsSourceValidated);
        Assert.True(PrWideArtifactParser.TryParseSynthesizedCandidates("{\"candidate_findings\":[" + FindingJson + "]}", out var wide));
        var wideFix = Assert.Single(wide).ToCandidateReviewFinding(candidate.Provenance).SuggestedFix;
        Assert.NotNull(wideFix);
        Assert.Equal("fixed<T>();", wideFix.ReplacementText);
        Assert.Null(wideFix.SourceHeadSha);
        Assert.False(wideFix.IsSourceValidated);
        Assert.Equal(fileFix, wideFix);
        var parsed = Assert.Single(QualityFilterExecutor.ParseResponse("{\"comments\":[" + FindingJson + "]}"));
        var unchanged = Assert.Single(
            QualityFilterExecutor.ParseResponse(
                "{\"comments\":[{\"file_path\":\"a.cs\",\"line_number\":2,\"severity\":\"suggestion\",\"message\":\"Exact input fails.\"}]}", [parsed]));
        Assert.Equal(parsed.SuggestedFix, unchanged.SuggestedFix);
        Assert.Equal(CommentSeverity.Error, unchanged.Severity);
    }

    [Fact]
    public void SynthesisGuidance_KeepsConcreteConditionalClaimAndOriginalReplacementAvailable()
    {
        var finding = new PrWideCandidateFinding(
            "finding", "If features.enabled: [preview] is supplied, the guard rejects it.", "configuration",
            new ConfidenceScore("configuration", 70), new EvidenceReference([], ["a.cs"], "resolved", "investigation"), ["a.cs"],
            CommentSeverity.Warning, "a.cs", 2)
        {
            SuggestedFix = new ReviewSuggestedFix(2, 2, "old();", "fixed();"),
        };
        var plan = new PrWideReviewPlan("plan", [], [], []);
        var input = ReviewPrompts.BuildPrWideSynthesisUserMessage(
            plan,
            [new PrWideInvestigationResult("task", "completed", [], [finding], [], false)]);
        var system = ReviewPrompts.BuildPrWideSynthesisSystemPrompt(new ReviewSystemContext(null, [], null));
        Assert.Contains(finding.Message, input);
        Assert.Contains("fixed();", input);
        Assert.Contains("original_text", input);
        Assert.Contains("no stronger than", system);
        Assert.Contains("unverified precondition", system);
        Assert.Contains("affected subsystem", system);
    }
}
