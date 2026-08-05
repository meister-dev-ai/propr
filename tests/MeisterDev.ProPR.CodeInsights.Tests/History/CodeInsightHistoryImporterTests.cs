// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.History;

namespace MeisterDev.ProPR.CodeInsights.Tests.History;

/// <summary>
///     The history import: it replays what the product already persisted through the live consumers, and reports
///     what it could not do rather than papering over it.
/// </summary>
public sealed class CodeInsightHistoryImporterTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset InWindow = new(2026, 5, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly To = new(2026, 5, 31);

    [Fact]
    public async Task ReplaysEachJobsFindingsThroughTheLiveIngestionPath()
    {
        var harness = new Harness();
        harness.SeedJob(findings: [("src/A.cs", 10), ("src/B.cs", 20)]);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        Assert.Equal(1, result.JobsRead);
        Assert.Equal(1, result.JobsImported);
        Assert.Equal(2, result.FindingsImported);
        Assert.Equal(1, result.PullRequests);

        // The same event the live path raises, so an imported finding is indistinguishable from a collected one.
        var evt = harness.Ingested.Single();
        Assert.Equal(ClientId, evt.ClientId);
        Assert.Equal(2, evt.Findings.Count);
        Assert.Equal([0, 1], evt.Findings.Select(finding => finding.Ordinal).ToArray());
        // Observed at the review's own time rather than at import time, or every imported finding would land in
        // today's bucket and the history would read as a spike.
        Assert.Equal(InWindow, evt.ObservedAt);
    }

    [Fact]
    public async Task SkipsJobsTheCollectionAlreadyHolds()
    {
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedCollectedFinding(jobId);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        // Skipped rather than merged: a finding is identified by its position in the job, and a replay cannot
        // promise the ordering the live capture used.
        Assert.Equal(1, result.JobsRead);
        Assert.Equal(0, result.JobsImported);
        Assert.Equal(1, result.JobsAlreadyCollected);
        Assert.Empty(harness.Ingested);
    }

    [Fact]
    public async Task AClosedGateImportsNothingAndSaysSo()
    {
        var harness = new Harness(gateOpen: false);
        harness.SeedJob(findings: [("src/A.cs", 10)]);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        Assert.True(result.CollectionDisabled);
        Assert.Equal(0, result.JobsRead);
        Assert.Empty(harness.Ingested);
    }

    [Fact]
    public async Task FindingsWhoseCommentsWereNeverLinkedToAThreadAreReportedAsSuch()
    {
        // The honest half of an import. Provenance was only recorded where thread retention was on, so on many
        // installations these findings can never gain an outcome, and the run says how many rather than leaving a
        // reader to wonder why recall stayed flat.
        var harness = new Harness();
        harness.SeedJob(findings: [("src/A.cs", 10)]);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        Assert.Equal(1, result.FindingsImported);
        Assert.Equal(1, result.FindingsWithoutThread);
        Assert.Null(harness.Ingested.Single().Findings[0].ProviderThreadId);
    }

    [Fact]
    public async Task AttachesTheThreadARetainedFindingWasPostedAs()
    {
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedRetainedThread(jobId, threadId: "5001", filePath: "src/A.cs", line: 10, resolved: true);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        Assert.Equal(0, result.FindingsWithoutThread);
        Assert.Equal("5001", harness.Ingested.Single().Findings[0].ProviderThreadId);
    }

    [Fact]
    public async Task OutcomesAreOnlyReplayedWhenAskedFor()
    {
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedRetainedThread(jobId, threadId: "5001", filePath: "src/A.cs", line: 10, resolved: true);

        var free = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        // The default run calls no model at all: findings, roll-ups and coverage cost nothing.
        Assert.Equal(0, free.OutcomeThreadsReplayed);
        await harness.Dispositions.DidNotReceive()
            .HandleThreadResolvedAsync(Arg.Any<ThreadResolvedDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithOutcomesAskedForItReplaysOurResolvedThreadsAndHandsTheRestToTheHarvester()
    {
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedRetainedThread(jobId, threadId: "5001", filePath: "src/A.cs", line: 10, resolved: true);
        harness.SeedRetainedThread(jobId: null, threadId: "6001", filePath: "src/C.cs", line: 3, resolved: true);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To, IncludeOutcomes: true));

        Assert.Equal(1, result.OutcomeThreadsReplayed);
        Assert.Equal(1, result.HumanThreadsReplayed);

        await harness.Dispositions.Received(1).HandleThreadResolvedAsync(
            Arg.Is<ThreadResolvedDomainEvent>(evt => evt.ThreadId == "5001"),
            Arg.Any<CancellationToken>());

        // A thread ProPR never posted goes to the harvester, which decides for itself whether it was a miss.
        await harness.Harvester.Received(1).HandleThreadObservedAsync(
            Arg.Is<ThreadUpdatedEvent>(evt => evt.ThreadId == "6001"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutcomesCanBeReplayedForJobsAnEarlierFindingsOnlyRunAlreadyCollected()
    {
        // The workflow this is built for: import findings for free first, then come back for outcomes. Those jobs
        // are already collected, so a run that only looked at pending jobs would never read their threads.
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedCollectedFinding(jobId);
        harness.SeedRetainedThread(jobId, threadId: "5001", filePath: "src/A.cs", line: 10, resolved: true);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To, IncludeOutcomes: true));

        Assert.Equal(1, result.JobsAlreadyCollected);
        Assert.Equal(0, result.JobsImported);
        Assert.Equal(1, result.OutcomeThreadsReplayed);
    }

    [Fact]
    public async Task ReachingTheJobBoundIsReported()
    {
        var harness = new Harness();
        harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedJob(findings: [("src/B.cs", 11)], pullRequestId: 8);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To, MaxJobs: 1));

        // So a reader knows another run will do more, rather than reading a partial import as the whole history.
        Assert.True(result.ReachedLimit);
        Assert.Equal(1, result.JobsRead);
    }

    [Fact]
    public async Task CarriedForwardResultsAreNotImportedAsNewFindings()
    {
        // Synthesis excludes carried-forward results when it assembles what a review publishes, so the live path
        // never collects them. Importing them would collect findings the reviewer was never credited with and give
        // one problem a second survival chain.
        var harness = new Harness();
        harness.SeedJob(findings: [("src/A.cs", 10)], carriedForward: [("src/Old.cs", 3)]);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        Assert.Equal(1, result.FindingsImported);
        Assert.Equal("src/A.cs", harness.Ingested.Single().Findings.Single().FilePath);
    }

    [Fact]
    public async Task AWindowHoldingExactlyTheBoundIsFinished()
    {
        // Reading one job past the bound is what makes this answerable: a run that merely filled its quota cannot
        // tell a full window from an exhausted one, and telling an operator to run it again for nothing is a lie.
        var harness = new Harness();
        harness.SeedJob(findings: [("src/A.cs", 10)]);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To, MaxJobs: 1));

        Assert.False(result.ReachedLimit);
        Assert.Equal(1, result.JobsImported);
    }

    [Fact]
    public async Task TwoFindingsOnOneLineEachTakeTheirOwnThread()
    {
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10), ("src/A.cs", 10)]);
        harness.SeedRetainedThread(jobId, threadId: "5001", filePath: "src/A.cs", line: 10, resolved: true);
        harness.SeedRetainedThread(jobId, threadId: "5002", filePath: "src/A.cs", line: 10, resolved: true);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        // Keeping only the first thread at a position would leave the second finding permanently unresolvable.
        Assert.Equal(0, result.FindingsWithoutThread);
        Assert.Equal(
            new[] { "5001", "5002" },
            harness.Ingested.Single().Findings.Select(finding => finding.ProviderThreadId).ToArray());
    }

    [Fact]
    public async Task AThreadWhoseIdIsNotNumericIsReplayedLikeAnyOther()
    {
        var harness = new Harness();
        var jobId = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedRetainedThread(jobId, threadId: "8f3c1a2e", filePath: "src/A.cs", line: 10, resolved: true);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To, IncludeOutcomes: true));

        Assert.Equal(1, result.OutcomeThreadsReplayed);
        await harness.Dispositions.Received(1).HandleThreadResolvedAsync(
            Arg.Is<ThreadResolvedDomainEvent>(evt => evt.ThreadId == "8f3c1a2e"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhatTheCollectionAlreadyHeldIsReportedAlongsideWhatWasImported()
    {
        var harness = new Harness();
        var collected = harness.SeedJob(findings: [("src/A.cs", 10)]);
        harness.SeedCollectedFinding(collected);
        harness.SeedJob(findings: [("src/B.cs", 20)], pullRequestId: 9);

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        // So that this run plus what was already there can be checked against what coverage says was produced.
        Assert.Equal(1, result.FindingsImported);
        Assert.Equal(1, result.FindingsAlreadyHeld);
    }

    [Fact]
    public async Task ReviewsOutsideTheWindowAreLeftAlone()
    {
        var harness = new Harness();
        harness.SeedJob(findings: [("src/A.cs", 10)], submittedAt: new DateTimeOffset(2026, 4, 2, 8, 0, 0, TimeSpan.Zero));

        var result = await harness.Importer.ImportAsync(new CodeInsightImportRequest(ClientId, From, To));

        Assert.Equal(0, result.JobsRead);
        Assert.Empty(harness.Ingested);
    }

    private sealed class Harness
    {
        private readonly MeisterProPRDbContext _dbContext;
        private readonly List<RetainedThreadView> _threads = [];
        private readonly List<PostedCommentOriginRow> _provenance = [];
        private int _nextPullRequestId = 7;

        public Harness(bool gateOpen = true)
        {
            var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseInMemoryDatabase($"import-{Guid.NewGuid()}")
                .Options;
            this._dbContext = new MeisterProPRDbContext(options);

            var gate = Substitute.For<ICodeInsightsCollectionGate>();
            gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(gateOpen);

            var ingestion = Substitute.For<ICodeInsightFindingIngestionService>();
            ingestion
                .When(service => service.HandleReviewFindingsProducedAsync(
                    Arg.Any<ReviewFindingsProducedEvent>(),
                    Arg.Any<CancellationToken>()))
                .Do(call => this.Ingested.Add(call.ArgAt<ReviewFindingsProducedEvent>(0)));

            var origins = Substitute.For<IPostedCommentOriginStore>();
            origins
                .GetJobIdsForPullRequestAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(_ => this._provenance);

            var archive = Substitute.For<IReviewArchiveStore>();
            archive
                .GetThreadsForPullRequestAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(_ => this._threads);

            this.Importer = new CodeInsightHistoryImporter(
                this._dbContext,
                gate,
                ingestion,
                NullLogger<CodeInsightHistoryImporter>.Instance,
                origins,
                archive,
                this.Dispositions,
                this.Harvester);
        }

        public CodeInsightHistoryImporter Importer { get; }

        public List<ReviewFindingsProducedEvent> Ingested { get; } = [];

        public ICodeInsightDispositionService Dispositions { get; } = Substitute.For<ICodeInsightDispositionService>();

        public ICodeInsightMissHarvester Harvester { get; } = Substitute.For<ICodeInsightMissHarvester>();

        public Guid SeedJob(
            IReadOnlyList<(string Path, int Line)> findings,
            int? pullRequestId = null,
            DateTimeOffset? submittedAt = null,
            IReadOnlyList<(string Path, int Line)>? carriedForward = null)
        {
            var jobId = Guid.NewGuid();
            var prId = pullRequestId ?? this._nextPullRequestId++;

            this._dbContext.ReviewJobs.Add(
                new ReviewJob(jobId, ClientId, "https://dev.azure.com/org", "project", "repo-1", prId, 1)
                {
                    SubmittedAt = submittedAt ?? InWindow,
                    CompletedAt = submittedAt ?? InWindow,
                    Status = JobStatus.Completed,
                });

            foreach (var group in findings.GroupBy(finding => finding.Path))
            {
                var fileResult = new ReviewFileResult(jobId, group.Key);
                fileResult.MarkCompleted(
                    "summary",
                    group
                        .Select(finding => new ReviewComment(
                            finding.Path,
                            finding.Line,
                            CommentSeverity.Warning,
                            $"finding at {finding.Path}:{finding.Line}"))
                        .ToList());
                this._dbContext.ReviewFileResults.Add(fileResult);
            }

            foreach (var carried in carriedForward ?? [])
            {
                // Built the way the review pipeline builds one: a copy of an earlier job's completed result.
                var prior = new ReviewFileResult(Guid.NewGuid(), carried.Path);
                prior.MarkCompleted(
                    "summary",
                    [new ReviewComment(carried.Path, carried.Line, CommentSeverity.Warning, "carried forward")]);
                this._dbContext.ReviewFileResults.Add(ReviewFileResult.CreateCarriedForward(jobId, prior));
            }

            this._dbContext.SaveChanges();
            return jobId;
        }

        /// <summary>Marks a job as already collected, the way a live capture or an earlier run would have.</summary>
        public void SeedCollectedFinding(Guid jobId)
        {
            var aggregateId = Guid.NewGuid();
            this._dbContext.CodeInsightPullRequests.Add(
                new CodeInsightPullRequest
                {
                    Id = aggregateId,
                    ClientId = ClientId,
                    RepositoryId = "repo-1",
                    PullRequestId = 7,
                    PullRequestState = "Active",
                    LatestRevisionKey = "1",
                    LastActivityAt = InWindow,
                    CreatedAt = InWindow,
                    UpdatedAt = InWindow,
                });

            this._dbContext.CodeInsightFindings.Add(
                new CodeInsightFinding
                {
                    Id = Guid.NewGuid(),
                    CodeInsightPullRequestId = aggregateId,
                    JobId = jobId,
                    RevisionKey = "1",
                    Ordinal = 0,
                    FilePath = "src/A.cs",
                    LineNumber = 10,
                    Severity = CommentSeverity.Warning,
                    EncryptedMessage = "cipher",
                    FindingChainId = Guid.NewGuid(),
                    ObservedAt = InWindow,
                    CreatedAt = InWindow,
                });

            this._dbContext.SaveChanges();
        }

        /// <summary>
        ///     A retained thread. Passing a job id records provenance for it too, which is what makes it one of
        ///     ProPR's own threads rather than somebody else's.
        /// </summary>
        public void SeedRetainedThread(Guid? jobId, string threadId, string filePath, int line, bool resolved)
        {
            var commentId = $"c-{threadId}";
            this._threads.Add(
                new RetainedThreadView(
                    threadId,
                    filePath,
                    line,
                    resolved ? "fixed" : "active",
                    InWindow,
                    [new RetainedCommentView(commentId, "author", jobId is not null, InWindow, "text", null)]));

            if (jobId is not null)
            {
                this._provenance.Add(new PostedCommentOriginRow(threadId, commentId, jobId.Value));
            }
        }
    }
}
