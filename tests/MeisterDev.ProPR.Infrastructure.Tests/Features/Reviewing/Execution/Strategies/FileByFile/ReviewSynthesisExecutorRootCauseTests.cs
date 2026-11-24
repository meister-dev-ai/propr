// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     The synthesis groups the findings of a pull request by root cause before the quality filter and the synthesis
///     call, so both receive each cause once, and the pull request receives one comment for it at the line of the primary
///     finding. A synthesized finding names the findings it rests on, and the publication policy leaves it out when all of
///     them are posted.
/// </summary>
public sealed class ReviewSynthesisExecutorRootCauseTests
{
    private const string HelperPath = "src/Cache/CacheKey.cs";
    private const string CallerPath = "src/Api/Orders.cs";
    private const string OtherPath = "src/Ui/Button.ts";
    private const string HelperMessage = "The cache key omits the tenant id.";
    private const string CallerMessage = "Orders read the cache entry of another tenant.";
    private const string OtherMessage = "The button submits the form twice.";
    private const string CrossCuttingMessage = "Tenant isolation is missing in the cache layer.";
    private const string SynthesisSummary = "The change adds a cache for orders.";

    [Fact]
    public async Task SynthesizeAsync_GroupedFindings_ReachTheSynthesisOnceAndPublishOneCommentThatListsTheOtherPlace()
    {
        var review = Review.Create();
        var grouper = GrouperJoining(HelperPath, CallerPath);
        var chat = ChatAnswering(SynthesisAnswer([]));

        var result = await review.SynthesizeAsync(chat, grouper: grouper);

        var request = UserMessages(chat).Single();
        Assert.Contains(review.FindingId(HelperPath), request);
        Assert.DoesNotContain(review.FindingId(CallerPath), request);
        Assert.Contains(review.FindingId(OtherPath), request);

        Assert.Equal(2, result.Comments.Count);
        Assert.DoesNotContain(result.Comments, comment => comment.FilePath == CallerPath);
        var survivor = Assert.Single(result.Comments, comment => comment.FilePath == HelperPath);
        Assert.Equal(12, survivor.LineNumber);
        Assert.StartsWith(HelperMessage, survivor.Message);
        Assert.Contains($"`{CallerPath}:40`: {CallerMessage}", survivor.Message);
        Assert.Equal([new ReviewCommentLocation(CallerPath, 40)], survivor.RelatedLocations);
        Assert.Equal(2, survivor.RootCauseGroupSize);
    }

    // The consolidation removes trailing whitespace from the message of the primary finding before it appends the other
    // places, so the comment of the group still keeps the identity of the finding that the file review produced.
    [Fact]
    public async Task SynthesizeAsync_GroupedFindingsWhosePrimaryMessageEndsInASpace_KeepThePerFileFindingId()
    {
        var review = Review.Create(helperMessage: HelperMessage + " ");
        var chat = ChatAnswering(SynthesisAnswer([]));

        await review.SynthesizeAsync(chat, grouper: GrouperJoining(HelperPath, CallerPath));

        var request = UserMessages(chat).Single();
        Assert.Contains(review.FindingId(HelperPath), request);
        Assert.DoesNotContain("finding-derived-", request);
    }

    // The comment of a group stands for every place of the group. A group whose primary finding lies outside the change
    // and one of whose places lies on a changed line is therefore not classified as outside the change, so a client that
    // does not post findings outside the changed lines still posts it.
    [Fact]
    public async Task SynthesizeAsync_GroupWithAPlaceOnAChangedLine_IsNotClassifiedAsOutsideTheChange()
    {
        var review = Review.Create();
        var diffs = new Dictionary<string, string> { [CallerPath] = "@@ -40 +40 @@\n-old\n+new" };

        var result = await review.SynthesizeAsync(ChatAnswering(SynthesisAnswer([])), grouper: GrouperJoining(HelperPath, CallerPath), diffs: diffs);

        var survivor = Assert.Single(result.Comments, comment => comment.FilePath == HelperPath);
        Assert.Null(survivor.ScopeRelation);
        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(WithholdOutOfScopeFindings: true), null);
        Assert.Contains(survivor, applied.PublishResult.Comments);
    }

    [Fact]
    public async Task SynthesizeAsync_GroupWhosePlacesAllLieOutsideTheChange_IsClassifiedAsOutsideTheChange()
    {
        var review = Review.Create();

        var result = await review.SynthesizeAsync(ChatAnswering(SynthesisAnswer([])), grouper: GrouperJoining(HelperPath, CallerPath));

        var survivor = Assert.Single(result.Comments, comment => comment.FilePath == HelperPath);
        Assert.Equal(ReviewCommentScopeRelation.OutsideChange, survivor.ScopeRelation);
        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(WithholdOutOfScopeFindings: true), null);
        Assert.DoesNotContain(survivor, applied.PublishResult.Comments);
    }

    [Fact]
    public async Task SynthesizeAsync_GroupedFindings_RecordTheMergeAndTheSpendOfTheGrouping()
    {
        var review = Review.Create();
        var grouper = GrouperJoining(HelperPath, CallerPath, new ModelCallSpend("verification-model", null, 500, 30, 0, 0, 0));
        var protocolId = Guid.NewGuid();
        review.Recorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(protocolId);

        await review.SynthesizeAsync(ChatAnswering(SynthesisAnswer([])), grouper: grouper);

        await review.Recorder.Received(1).AddTokensAsync(
            protocolId, 500, 30, AiConnectionModelCategory.Default, "verification-model", Arg.Any<CancellationToken>(), 0, 0, 0, null);
        await review.Recorder.Received(1).RecordVerificationEventAsync(
            protocolId,
            ReviewProtocolEventNames.FindingDeduplication,
            Arg.Is<string?>(details => details != null && details.Contains("\"stage\":\"root_cause\"") && details.Contains("\"removedCount\":1")),
            Arg.Is<string?>(output => output != null && output.Contains("\"reason\":\"root_cause\"") && output.Contains("\"lineNumber\":40")),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SynthesizeAsync_GroupedFindings_ReachTheQualityFilterOnce()
    {
        var review = Review.Create(new AiReviewOptions { QualityFilterThreshold = 1 });
        var grouper = GrouperJoining(HelperPath, CallerPath);
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "The filter keeps every comment.")),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, SynthesisAnswer([]))));

        await review.SynthesizeAsync(chat, grouper: grouper, fileFindingsJudged: false);

        var filterRequest = UserMessages(chat).First();
        Assert.Contains(RootCauseFindingConsolidator.AlsoReportedAtHeading, filterRequest);
        Assert.Contains("finding-filter-002", filterRequest);
        Assert.DoesNotContain("finding-filter-003", filterRequest);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SynthesizeAsync_GroupingTurnedOffOrSkipped_PublishesEveryFinding(bool enabled, bool skipped)
    {
        var review = Review.Create(new AiReviewOptions { EnableRootCauseGrouping = enabled });
        var grouper = GrouperJoining(HelperPath, CallerPath);
        var context = new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>())
        {
            SkippedSteps = skipped ? new ReviewStepSkips([FileByFileReviewStepIds.RootCauseGrouping]) : new ReviewStepSkips(),
        };

        var result = await review.SynthesizeAsync(ChatAnswering(SynthesisAnswer([])), grouper: grouper, context: context);

        Assert.Equal(3, result.Comments.Count);
        await grouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default, default, default);
    }

    // The synthesis publishes the concern together with the identifiers of the findings it rests on. Whether the
    // comments of those findings reach the pull request is known only after the client's settings, so the publication
    // policy leaves the concern out and the review record keeps it as a withheld finding.
    [Fact]
    public async Task SynthesizeAsync_SynthesizedFindingWhoseSupportingFindingsAllPublish_IsLeftOutByThePublicationPolicyAndTheSummaryStays()
    {
        var review = Review.Create();
        var chat = ChatAnswering(SynthesisAnswer([review.FindingId(HelperPath), review.FindingId(CallerPath)]));

        var result = await review.SynthesizeAsync(chat, gate: GatePublishingAllBut());

        var concern = Assert.Single(result.Comments, comment => comment.Message == CrossCuttingMessage);
        Assert.Equal([review.FindingId(HelperPath), review.FindingId(CallerPath)], concern.SupportingFindingIds);
        Assert.Equal(review.FindingId(HelperPath), Assert.Single(result.Comments, comment => comment.FilePath == HelperPath).FindingId);
        Assert.Equal(review.FindingId(CallerPath), Assert.Single(result.Comments, comment => comment.FilePath == CallerPath).FindingId);
        Assert.Null(Assert.Single(result.Comments, comment => comment.FilePath == OtherPath).FindingId);
        Assert.StartsWith(SynthesisSummary, result.Summary);
        Assert.DoesNotContain("Summary-only findings:", result.Summary);

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(), null);

        Assert.Equal(3, applied.PublishResult.Comments.Count);
        Assert.DoesNotContain(concern, applied.PublishResult.Comments);
        var withheld = Assert.Single(applied.RecordResult.WithheldFindings, finding => finding.Comment.Message == CrossCuttingMessage);
        Assert.Equal(ReviewFindingGateReasonCodes.RestatesPublishedFindings, withheld.ReasonCode);
        Assert.Equal(applied.PublishResult.Summary, result.Summary);
    }

    [Fact]
    public async Task SynthesizeAsync_SynthesizedFindingWithASupportingFindingThatIsNotPublished_IsPublished()
    {
        var review = Review.Create();
        var chat = ChatAnswering(SynthesisAnswer([review.FindingId(HelperPath), review.FindingId(CallerPath)]));

        var result = await review.SynthesizeAsync(chat, gate: GatePublishingAllBut(review.FindingId(CallerPath)));

        var concern = Assert.Single(result.Comments, comment => comment.Message == CrossCuttingMessage);
        Assert.DoesNotContain(result.WithheldFindings, finding => finding.ReasonCode == ReviewFindingGateReasonCodes.RestatesPublishedFindings);
        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(), null);
        Assert.Contains(concern, applied.PublishResult.Comments);
        Assert.Equal(0, applied.RestatementsNotPosted);
    }

    // Joins the findings at the two paths into one group with the first as its primary.
    private static IFindingRootCauseGrouper GrouperJoining(string primaryPath, string memberPath, ModelCallSpend? spend = null)
    {
        var grouper = Substitute.For<IFindingRootCauseGrouper>();
        grouper.GroupAsync(Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<Guid>(), Arg.Any<ReviewSystemContext?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var comments = call.Arg<IReadOnlyList<ReviewComment>>().ToList();
                var primary = comments.FindIndex(comment => comment.FilePath == primaryPath);
                var member = comments.FindIndex(comment => comment.FilePath == memberPath);
                return new FindingRootCauseGrouping(
                    [new FindingRootCauseGroup([Math.Min(primary, member), Math.Max(primary, member)], primary, "Cache key without tenant id")],
                    spend is null ? [] : [spend]);
            });
        return grouper;
    }

    // Publishes every finding except the named ones, which stay summary-only.
    private static IDeterministicReviewFindingGate GatePublishingAllBut(params string[] heldBack)
    {
        var gate = Substitute.For<IDeterministicReviewFindingGate>();
        gate.EvaluateAsync(Arg.Any<IReadOnlyList<CandidateReviewFinding>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<FinalGateDecision>)call.Arg<IReadOnlyList<CandidateReviewFinding>>()
                .Select(finding => heldBack.Contains(finding.FindingId)
                    ? new FinalGateDecision(
                        finding.FindingId, FinalGateDecision.SummaryOnlyDisposition, [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
                        "test_rules", [], finding.Evidence, null)
                    : new FinalGateDecision(
                        finding.FindingId, FinalGateDecision.PublishDisposition, [ReviewFindingGateReasonCodes.DefaultPublish], "test_rules", [],
                        finding.Evidence, null))
                .ToList());
        return gate;
    }

    private static string SynthesisAnswer(IReadOnlyList<string> supportingFindingIds)
    {
        return JsonSerializer.Serialize(
            new
            {
                summary = SynthesisSummary,
                summary_finding_ids = Array.Empty<string>(),
                finding_challenges = Array.Empty<object>(),
                cross_cutting_concerns = supportingFindingIds.Count == 0
                    ? Array.Empty<object>()
                    :
                    [
                        new
                        {
                            message = CrossCuttingMessage,
                            severity = "warning",
                            category = "cross_cutting",
                            candidateSummaryText = "Tenant isolation is missing.",
                            supportingFindingIds,
                            supportingFiles = new[] { HelperPath, CallerPath },
                            evidenceResolutionState = "resolved",
                            evidenceSource = "synthesis_payload",
                        },
                    ],
            });
    }

    private static IChatClient ChatAnswering(string answer)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        return chat;
    }

    private static IReadOnlyList<string> UserMessages(IChatClient chat) => chat.ReceivedCalls()
        .Select(call => ((IEnumerable<ChatMessage>)call.GetArguments()[0]!).Single(message => message.Role == ChatRole.User).Text)
        .ToList();

    /// <summary>A job with three reviewed files, one finding in each, and the synthesis executor that combines them.</summary>
    private sealed class Review
    {
        private readonly Dictionary<string, ReviewFileResult> _files = new(StringComparer.Ordinal);

        private Review(AiReviewOptions options, string helperMessage)
        {
            this.Options = options;
            this.Job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 1, 1);
            this.AddFile(new ReviewComment(HelperPath, 12, CommentSeverity.Warning, helperMessage));
            this.AddFile(new ReviewComment(CallerPath, 40, CommentSeverity.Warning, CallerMessage));
            this.AddFile(new ReviewComment(OtherPath, 3, CommentSeverity.Warning, OtherMessage));
            this.Store.GetByIdWithFileResultsAsync(this.Job.Id, Arg.Any<CancellationToken>()).Returns(this.Job);
        }

        public ReviewJob Job { get; }

        public AiReviewOptions Options { get; }

        public IReviewFileResultStore Store { get; } = Substitute.For<IReviewFileResultStore>();

        public IProtocolRecorder Recorder { get; } = Substitute.For<IProtocolRecorder>();

        public static Review Create(AiReviewOptions? options = null, string helperMessage = HelperMessage) =>
            new(options ?? new AiReviewOptions(), helperMessage);

        public string FindingId(string path) => FileByFileReviewOrchestrator.BuildPerFileFindingId(this._files[path], 1);

        public Task<ReviewResult> SynthesizeAsync(
            IChatClient chat,
            IFindingRootCauseGrouper? grouper = null,
            IDeterministicReviewFindingGate? gate = null,
            ReviewSystemContext? context = null,
            bool fileFindingsJudged = true,
            IReadOnlyDictionary<string, string>? diffs = null)
        {
            var logger = Substitute.For<ILogger<FileByFileReviewOrchestrator>>();
            var sut = new ReviewSynthesisExecutor(
                this.Store, this.Recorder, logger, this.Options, new CandidateFindingFactory(null), new QualityFilterExecutor(this.Options, logger),
                null, gate ?? new DeterministicReviewFindingGate(), null, null, null, chat, findingRootCauseGrouper: grouper);
            var pr = new PullRequest(
                "https://dev.azure.com/org", "project", "repo", "repo", 1, 1, "Order cache", null, "source", "main",
                this._files.Keys
                    .Select(path => new ChangedFile(path, ChangeType.Edit, "content", diffs?.GetValueOrDefault(path) ?? "@@ -1 +1 @@\n-old\n+new"))
                    .ToList());
            return sut.SynthesizeAsync(
                this.Job, pr, context ?? new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>()), chat, [],
                FileReviewDispatchPlanner.BudgetSoftCapSummary.None, CancellationToken.None, fileFindingsJudged);
        }

        private void AddFile(ReviewComment comment)
        {
            var file = new ReviewFileResult(this.Job.Id, comment.FilePath!);
            file.MarkCompleted($"Summary of {comment.FilePath}.", [comment]);
            this.Job.FileReviewResults.Add(file);
            this._files[comment.FilePath!] = file;
        }
    }
}
