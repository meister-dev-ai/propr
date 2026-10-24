// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

public sealed class ReviewSynthesisExecutorSummaryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SynthesizeAsync_WithoutGateGroundsChallengedSummaryAgainstEveryPublishedGroup(bool synthesized, bool prWide)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 1, 1);
        var file = new ReviewFileResult(job.Id, "a.cs");
        file.MarkCompleted(
            "The retry loop ignores the update.", [new ReviewComment("a.cs", 1, CommentSeverity.Warning, "The loop ignores the latest update.")]);
        job.FileReviewResults.Add(file);
        var store = Substitute.For<IReviewFileResultStore>();
        store.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var findingId = FileByFileReviewOrchestrator.BuildPerFileFindingId(file, 1);
        var response = JsonSerializer.Serialize(
            new
            {
                summary = "The loop ignores the latest update.",
                summary_finding_ids = new[] { findingId },
                finding_challenges = new[]
                    { new { finding_id = findingId, reason = "Check the assignment.", counterevidence = "The accumulator includes the new value." } },
                cross_cutting_concerns = synthesized
                    ? new[] { new { message = "A separately supported cross-file defect.", severity = "warning", category = "cross_cutting" } }
                    : [],
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        var judge = Substitute.For<IReviewFindingVerifier>();
        judge.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item => new VerificationOutcome(
                item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.ContradictedKind, FinalGateDecision.DropDisposition,
                ["source_counterevidence_refutes_claim"], [], VerificationOutcome.StrongEvidence,
                "The assignment includes the latest value.", VerificationOutcome.AiMicroVerifierEvaluator, false)).ToArray());
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new EvidenceBundle(call.Arg<VerificationWorkItem>().Claim.ClaimId, [], EvidenceBundle.PartialCoverage));
        var recorder = Substitute.For<IProtocolRecorder>();
        var options = new AiReviewOptions();
        var sut = new ReviewSynthesisExecutor(
            store, recorder, Substitute.For<ILogger<FileByFileReviewOrchestrator>>(), options,
            new CandidateFindingFactory(null), new QualityFilterExecutor(options, Substitute.For<ILogger<FileByFileReviewOrchestrator>>()),
            new PrLevelReviewVerificationExecutor(new DeterministicReviewClaimExtractor(), collector, recorder, options, judge),
            null, null, null, null, chat);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Retry update", null,
            "source", "main", [new ChangedFile("a.cs", ChangeType.Edit, "value = latest;", "@@ -1 +1 @@\n-old\n+value = latest;")]);
        CandidateReviewFinding[] prWideFindings = prWide
            ?
            [
                new CandidateReviewFinding(
                    "finding-wide", new CandidateFindingProvenance(CandidateFindingProvenance.PrWidePassOrigin, "pr_wide"),
                    CommentSeverity.Warning, "Another independently supported PR-wide finding.", CandidateReviewFinding.CrossCuttingCategory)
            ]
            : [];

        var result = await sut.SynthesizeAsync(
            job, pr, new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>()), chat,
            prWideFindings, FileReviewDispatchPlanner.BudgetSoftCapSummary.None, CancellationToken.None, fileFindingsJudged: true);

        var count = (synthesized ? 1 : 0) + (prWide ? 1 : 0);
        Assert.Equal(count, result.Comments.Count);
        Assert.Contains($"Verification retained {count} publishable finding", result.Summary);
        Assert.DoesNotContain("No publishable", result.Summary);
        Assert.DoesNotContain("The loop ignores the latest update.", result.Summary);
        Assert.Equal("The loop ignores the latest update.", Assert.Single(result.WithheldFindings).Comment.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynthesizeAsync_ChallengeLeftOpen_KeepsTheFindingOutOfThePublishedSummary(bool skipFinalGate)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 1, 1);
        var file = new ReviewFileResult(job.Id, "a.cs");
        file.MarkCompleted(
            "The retry loop ignores the update.", [new ReviewComment("a.cs", 1, CommentSeverity.Warning, "The loop ignores the latest update.")]);
        job.FileReviewResults.Add(file);
        var store = Substitute.For<IReviewFileResultStore>();
        store.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var findingId = FileByFileReviewOrchestrator.BuildPerFileFindingId(file, 1);
        var response = JsonSerializer.Serialize(
            new
            {
                summary = "The retry loop changes how updates are applied.",
                summary_finding_ids = new[] { findingId },
                finding_challenges = new[]
                    { new { finding_id = findingId, reason = "Check the assignment.", counterevidence = "The accumulator includes the new value." } },
                cross_cutting_concerns = Array.Empty<object>(),
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        var judge = Substitute.For<IReviewFindingVerifier>();
        judge.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item => new VerificationOutcome(
                item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.InsufficientEvidenceKind, FinalGateDecision.SummaryOnlyDisposition,
                ["insufficient_context"], [], VerificationOutcome.WeakEvidence,
                "The excerpt did not show the assignment.", VerificationOutcome.AiMicroVerifierEvaluator, false)).ToArray());
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new EvidenceBundle(call.Arg<VerificationWorkItem>().Claim.ClaimId, [], EvidenceBundle.PartialCoverage));
        var recorder = Substitute.For<IProtocolRecorder>();
        var options = new AiReviewOptions();
        var sut = new ReviewSynthesisExecutor(
            store, recorder, Substitute.For<ILogger<FileByFileReviewOrchestrator>>(), options,
            new CandidateFindingFactory(null), new QualityFilterExecutor(options, Substitute.For<ILogger<FileByFileReviewOrchestrator>>()),
            new PrLevelReviewVerificationExecutor(new DeterministicReviewClaimExtractor(), collector, recorder, options, judge),
            new DeterministicReviewFindingGate(), null, null, null, chat);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Retry update", null,
            "source", "main", [new ChangedFile("a.cs", ChangeType.Edit, "value = latest;", "@@ -1 +1 @@\n-old\n+value = latest;")]);
        var context = new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>())
        {
            SkippedSteps = skipFinalGate ? new ReviewStepSkips([FileByFileReviewStepIds.FinalGate]) : new ReviewStepSkips(),
        };

        var result = await sut.SynthesizeAsync(
            job, pr, context, chat, [], FileReviewDispatchPlanner.BudgetSoftCapSummary.None, CancellationToken.None, fileFindingsJudged: true);

        Assert.Empty(result.Comments);
        Assert.Equal("The loop ignores the latest update.", Assert.Single(result.WithheldFindings).Comment.Message);
        Assert.DoesNotContain("Summary-only findings:", result.Summary);
    }

    // The summary is written before the gate, so it can describe a finding the gate then drops. The synthesis model
    // rewrites it without that finding, and the reader keeps the description of the change.
    [Fact]
    public async Task SynthesizeAsync_SummaryDescribingADroppedFinding_IsRewrittenWithoutIt()
    {
        var (result, chat) =
            await SynthesizeWithRefutedChallengeAsync(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, "The change renames the retry setting.")));

        Assert.StartsWith("The change renames the retry setting.", result.Summary);
        Assert.DoesNotContain("The loop ignores the latest update.", result.Summary);
        Assert.DoesNotContain("dropped during verification", result.Summary);

        var request = chat.ReceivedCalls()
            .Select(call => (IEnumerable<ChatMessage>)call.GetArguments()[0]!)
            .Last()
            .Single(message => message.Role == ChatRole.User)
            .Text;
        var removed = request[
            request.IndexOf("REMOVED FINDINGS:", StringComparison.Ordinal)..request.IndexOf("FINDINGS THAT REMAIN:", StringComparison.Ordinal)];
        Assert.Contains("The loop ignores the latest update.", removed);
    }

    [Fact]
    public async Task SynthesizeAsync_FailedSummaryRewrite_KeepsTheCountOfDroppedFindings()
    {
        var (result, _) = await SynthesizeWithRefutedChallengeAsync(() => throw new HttpRequestException("The provider is unavailable."));

        Assert.Contains("1 candidate finding was dropped during verification.", result.Summary);
        Assert.DoesNotContain("The loop ignores the latest update.", result.Summary);
    }

    // A blank answer leaves no summary, and a JSON object would reach the pull request as raw JSON.
    [Theory]
    [InlineData("   ")]
    [InlineData("""{"summary":"The change renames the retry setting."}""")]
    public async Task SynthesizeAsync_SummaryRewriteWithoutSummaryText_KeepsTheCountOfDroppedFindings(string answer)
    {
        var (result, _) = await SynthesizeWithRefutedChallengeAsync(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));

        Assert.StartsWith("1 candidate finding was dropped during verification.", result.Summary);
    }

    /// <summary>
    ///     Synthesizes one file whose only finding the summary describes and synthesis challenges, and whose
    ///     reconsideration refutes it, so the gate drops a finding the summary names.
    /// </summary>
    /// <param name="rewrite">The answer to the second model call, which rewrites the summary.</param>
    private static async Task<(ReviewResult Result, IChatClient Chat)> SynthesizeWithRefutedChallengeAsync(Func<ChatResponse> rewrite)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 1, 1);
        var file = new ReviewFileResult(job.Id, "a.cs");
        file.MarkCompleted(
            "The retry loop ignores the update.", [new ReviewComment("a.cs", 1, CommentSeverity.Warning, "The loop ignores the latest update.")]);
        job.FileReviewResults.Add(file);
        var store = Substitute.For<IReviewFileResultStore>();
        store.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        var findingId = FileByFileReviewOrchestrator.BuildPerFileFindingId(file, 1);
        var synthesis = JsonSerializer.Serialize(
            new
            {
                summary = "The loop ignores the latest update. The change also renames the retry setting.",
                summary_finding_ids = new[] { findingId },
                finding_challenges = new[]
                    { new { finding_id = findingId, reason = "Check the assignment.", counterevidence = "The accumulator includes the new value." } },
                cross_cutting_concerns = Array.Empty<object>(),
            });
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesis)), _ => rewrite());
        var judge = Substitute.For<IReviewFindingVerifier>();
        judge.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item => new VerificationOutcome(
                item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.ContradictedKind, FinalGateDecision.DropDisposition,
                ["source_counterevidence_refutes_claim"], [], VerificationOutcome.StrongEvidence,
                "The assignment includes the latest value.", VerificationOutcome.AiMicroVerifierEvaluator, false)).ToArray());
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new EvidenceBundle(call.Arg<VerificationWorkItem>().Claim.ClaimId, [], EvidenceBundle.PartialCoverage));
        var recorder = Substitute.For<IProtocolRecorder>();
        var options = new AiReviewOptions();
        var sut = new ReviewSynthesisExecutor(
            store, recorder, Substitute.For<ILogger<FileByFileReviewOrchestrator>>(), options,
            new CandidateFindingFactory(null), new QualityFilterExecutor(options, Substitute.For<ILogger<FileByFileReviewOrchestrator>>()),
            new PrLevelReviewVerificationExecutor(new DeterministicReviewClaimExtractor(), collector, recorder, options, judge),
            new DeterministicReviewFindingGate(), null, null, null, chat);
        var pr = new PullRequest(
            "https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Retry update", null,
            "source", "main", [new ChangedFile("a.cs", ChangeType.Edit, "value = latest;", "@@ -1 +1 @@\n-old\n+value = latest;")]);

        var result = await sut.SynthesizeAsync(
            job, pr, new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>()), chat,
            [], FileReviewDispatchPlanner.BudgetSoftCapSummary.None, CancellationToken.None, fileFindingsJudged: true);

        return (result, chat);
    }

    // A review with more findings than the request lists keeps the findings that matter to the summary: a dropped finding
    // the summary describes, and a remaining finding the pull request shows.
    [Fact]
    public void BuildSummaryRewriteUserMessage_MoreFindingsThanTheBound_ListsTheDescribedAndShownFindingsFirst()
    {
        var droppedIds = Enumerable.Range(0, 200).Select(index => $"dropped-{index:000}").ToArray();
        var keptIds = Enumerable.Range(0, 200).Select(index => $"kept-{index:000}").ToArray();
        var findings = droppedIds.Concat(keptIds).Append("published").Select(RewriteFinding).ToList();
        var decisions = droppedIds.Select(id => RewriteDecision(id, FinalGateDecision.DropDisposition))
            .Concat(keptIds.Select(id => RewriteDecision(id, FinalGateDecision.SummaryOnlyDisposition)))
            .Append(RewriteDecision("published", FinalGateDecision.PublishDisposition))
            .ToList();
        var reconciled = new SummaryReconciliationResult(
            "The summary describes dropped-199.", "2 candidate findings were dropped during verification.", droppedIds, [], true,
            "deterministic_summary_rewrite");

        var request = ReviewSynthesisExecutor.BuildSummaryRewriteUserMessage(reconciled, findings, decisions, ["dropped-199"]);

        var removedStart = request.IndexOf("REMOVED FINDINGS:", StringComparison.Ordinal);
        var remainingStart = request.IndexOf("FINDINGS THAT REMAIN:", StringComparison.Ordinal);
        Assert.Contains("dropped-199", request[removedStart..remainingStart]);
        Assert.Contains("published", request[remainingStart..]);
        Assert.DoesNotContain("kept-199", request[remainingStart..]);
    }

    private static CandidateReviewFinding RewriteFinding(string findingId) => new(
        findingId,
        new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
        CommentSeverity.Warning,
        $"Finding {findingId} describes a defect.",
        CandidateReviewFinding.PerFileCommentCategory,
        "src/Foo.cs",
        12);

    private static FinalGateDecision RewriteDecision(string findingId, string disposition) => new(
        findingId,
        disposition,
        [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
        "verification_outcome_rules",
        [],
        null,
        null);
}
