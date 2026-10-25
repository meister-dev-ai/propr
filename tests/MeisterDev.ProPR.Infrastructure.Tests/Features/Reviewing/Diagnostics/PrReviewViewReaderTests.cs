// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

[Collection("PostgresIntegration1")]
public sealed class PrReviewViewReaderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string Scope = "https://provider.example";
    private const string Project = "project";
    private const string Repository = "repository";
    private const int PullRequest = 373;
    private static readonly Guid ClientId = Guid.Parse("deb21d85-b42f-43b4-9059-a6e02ec89ffd");
    private static readonly Guid OtherClientId = Guid.Parse("deb21d85-b42f-43b4-9059-a6e02ec89ffe");
    private MeisterProPRDbContext _db = null!;
    private EfPrReviewViewReader _reader = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._db = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);
        await this._db.ReviewJobs.ExecuteDeleteAsync();
        await this._db.ThreadPassJobs.ExecuteDeleteAsync();
        await this._db.ReviewPrScans.ExecuteDeleteAsync();
        foreach (var clientId in new[] { ClientId, OtherClientId })
        {
            if (!await this._db.Clients.AnyAsync(client => client.Id == clientId))
            {
                this._db.Clients.Add(
                    new ClientRecord
                    {
                        Id = clientId,
                        TenantId = TenantCatalog.SystemTenantId,
                        DisplayName = "PR summary test client",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        await this._db.SaveChangesAsync();
        this._reader = new EfPrReviewViewReader(this._db);
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadAsync_CountsFindingsWithoutDeserializingUnrelatedResultOrEventFields()
    {
        var job = CreateJob();
        job.TokenBreakdown.Add(
            new TokenBreakdownEntry(
                AiConnectionModelCategory.HighEffort, "model", 300, 30,
                TotalCachedInputTokens: 40, EstimatedCostUsd: 0.25m));
        var protocol = AddProtocol(job, 100, 10);
        AddProtocol(job, 200, 20);
        var contribution = AddEvent(protocol, "memory_reconsideration_completed", "{\"contributingMemoryIds\":[]}");
        AddEvent(protocol, "ai_call", new string('x', 200_000));
        this._db.ReviewJobs.Add(job);
        await this._db.SaveChangesAsync();
        await this._db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE review_jobs SET result_json = '{{\"Summary\":123,\"Comments\":[{{}},{{}}],\"WithheldFindings\":false}}'::jsonb, model_evidence_manifest_json = 'false'::jsonb WHERE id = {job.Id}");
        await this._db.Database.ExecuteSqlInterpolatedAsync($"UPDATE protocol_events SET phase_timings = 'false'::jsonb WHERE id = {contribution.Id}");
        this._db.ChangeTracker.Clear();

        var data = await this.ReadAsync();

        var summary = Assert.Single(data.Jobs);
        Assert.Equal(job.Id, summary.JobId);
        Assert.Equal(2, summary.FindingCount);
        Assert.Equal(300, summary.TotalInputTokens);
        Assert.Equal(30, summary.TotalOutputTokens);
        Assert.Equal(job.TokenBreakdown, summary.TokenBreakdown);
        Assert.Equal(contribution.InputTextSample, Assert.Single(data.ContributingMemoryPayloads));
        Assert.Empty(this._db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadAsync_UsesStoredTotalsIndependentlyAndPreservesAbsentResults()
    {
        var storedInput = CreateJob(submittedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        AddProtocol(storedInput, 100, 10);
        var storedOutput = CreateJob(submittedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        AddProtocol(storedOutput, 200, 20);
        var empty = CreateJob(submittedAt: DateTimeOffset.UtcNow.AddMinutes(-3));
        this._db.ReviewJobs.AddRange(storedInput, storedOutput, empty);
        this._db.Entry(storedInput).Property(job => job.TotalInputTokensAggregated).CurrentValue = 0;
        this._db.Entry(storedOutput).Property(job => job.TotalOutputTokensAggregated).CurrentValue = 9;
        await this._db.SaveChangesAsync();
        this._db.ChangeTracker.Clear();

        var data = await this.ReadAsync();

        Assert.Collection(
            data.Jobs,
            job =>
            {
                Assert.Equal(0, job.TotalInputTokens);
                Assert.Equal(10, job.TotalOutputTokens);
            },
            job =>
            {
                Assert.Equal(200, job.TotalInputTokens);
                Assert.Equal(9, job.TotalOutputTokens);
            },
            job =>
            {
                Assert.Equal(0, job.TotalInputTokens);
                Assert.Equal(0, job.TotalOutputTokens);
            });
        Assert.All(data.Jobs, job => Assert.Null(job.FindingCount));
        Assert.Empty(this._db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadAsync_FiltersEveryCoordinateAndLimitsContributionsToTheRequestedPage()
    {
        var first = CreateJob(submittedAt: DateTimeOffset.UtcNow);
        AddEvent(AddProtocol(first), "memory_reconsideration_completed", "first");
        var second = CreateJob(submittedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        AddEvent(AddProtocol(second), "memory_reconsideration_completed", "second");
        var others = new[]
        {
            CreateJob(clientId: OtherClientId), CreateJob(scope: "https://other.example"),
            CreateJob(project: "other"), CreateJob(repository: "other"), CreateJob(pullRequest: PullRequest + 1),
        };
        foreach (var other in others)
        {
            AddEvent(AddProtocol(other), "memory_reconsideration_completed", "excluded");
        }

        this._db.ReviewJobs.AddRange(others.Append(first).Append(second));
        await this._db.SaveChangesAsync();
        this._db.ChangeTracker.Clear();

        var data = await this.ReadAsync(page: 2, pageSize: 1);

        Assert.Equal(second.Id, Assert.Single(data.Jobs).JobId);
        Assert.Equal("second", Assert.Single(data.ContributingMemoryPayloads));
        var empty = await this.ReadAsync(page: 3, pageSize: 1);
        Assert.Empty(empty.Jobs);
        Assert.Empty(empty.ContributingMemoryPayloads);
    }

    [Fact]
    public async Task ReadAsync_OrdersOnlyContributionSamplesByJobProtocolAndEvent()
    {
        var job = CreateJob();
        var earlierAttempt = AddProtocol(job, attempt: 1);
        var laterAttempt = AddProtocol(job, attempt: 2);
        var start = DateTimeOffset.UtcNow;
        AddEvent(laterAttempt, "memory_reconsideration_completed", "later event", start.AddSeconds(2));
        AddEvent(laterAttempt, "memory_reconsideration_completed", "first event", start);
        AddEvent(earlierAttempt, "memory_reconsideration_completed", "earlier attempt", start);
        AddEvent(laterAttempt, "other_event", "unrelated", start);
        AddEvent(laterAttempt, "memory_reconsideration_completed", null, start);
        this._db.ReviewJobs.Add(job);
        await this._db.SaveChangesAsync();
        this._db.ChangeTracker.Clear();

        var data = await this.ReadAsync();

        Assert.Equal(new[] { "first event", "later event", "earlier attempt" }, data.ContributingMemoryPayloads);
    }

    [Fact]
    public async Task ReadAsync_ProjectsTheNewestFiftyThreadPassesWithHandledCountsAndSpend()
    {
        var start = DateTimeOffset.UtcNow;
        var passes = Enumerable.Range(0, 52).Select(index => CreatePass(index, start.AddMinutes(index))).ToArray();
        passes[^1].AccumulateSpend(900, 120, 0.75m);
        passes[^1].SetBudgetBlock(BudgetScopeKind.Increment, BudgetCapKind.Soft, 5m, 6m);
        foreach (var index in Enumerable.Range(0, 3))
        {
            passes[^1].HandledThreads.Add(
                new ThreadPassHandledThread
                {
                    Id = Guid.NewGuid(), ThreadPassJobId = passes[^1].Id, ClientId = ClientId, RepositoryId = Repository,
                    PullRequestId = PullRequest, ThreadId = index.ToString(), ObservedReplyCount = 1, RecordedAt = start,
                });
        }

        var otherClient = CreatePass(100, start.AddHours(10), clientId: OtherClientId);
        var otherScope = CreatePass(101, start.AddHours(10), scope: "https://other.example");
        var otherProject = CreatePass(102, start.AddHours(10), project: "other");
        var otherRepo = CreatePass(103, start.AddHours(10), repository: "other");
        var otherPr = CreatePass(104, start.AddHours(10), pullRequest: PullRequest + 1);
        this._db.ThreadPassJobs.AddRange(passes.Concat([otherClient, otherScope, otherProject, otherRepo, otherPr]));
        await this._db.SaveChangesAsync();
        this._db.ChangeTracker.Clear();

        var data = await this.ReadAsync();

        Assert.Equal(50, data.ThreadPasses.Count);
        var summary = data.ThreadPasses[0];
        Assert.Equal(passes[^1].Id, summary.ThreadPassId);
        Assert.Equal(passes[2].Id, data.ThreadPasses[^1].ThreadPassId);
        Assert.Equal(3, summary.ThreadCount);
        Assert.Equal(900, summary.TotalInputTokens);
        Assert.Equal(120, summary.TotalOutputTokens);
        Assert.Equal(0.75m, summary.TotalEstimatedCostUsd);
        Assert.Equal(BudgetScopeKind.Increment, summary.BudgetBlockScope);
        Assert.Equal(6m, summary.BudgetBlockSpentUsd);
        Assert.Empty(this._db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadAsync_ReadsWatermarksWithoutTrackingScanThreads()
    {
        var detectedAt = DateTimeOffset.Parse("2026-10-25T12:00:00+00:00");
        var scan = new ReviewPrScan(Guid.NewGuid(), ClientId, Scope, Project, Repository, PullRequest, "reviewed")
        {
            PendingReviewRevisionKey = "pending", PendingReviewDetectedAt = detectedAt,
        };
        for (var index = 0; index < 100; index++)
        {
            scan.Threads.Add(new ReviewPrScanThread { ReviewPrScanId = scan.Id, ThreadId = index.ToString() });
        }

        this._db.ReviewPrScans.Add(scan);
        await this._db.SaveChangesAsync();
        this._db.ChangeTracker.Clear();

        var data = await this.ReadAsync();

        Assert.Equal(new PrReviewWatermark("pending", "reviewed", detectedAt), data.Watermark);
        Assert.Empty(this._db.ChangeTracker.Entries());
        Assert.Null((await this._reader.ReadAsync(OtherClientId, Scope, Project, Repository, PullRequest, 1, 20)).Watermark);
    }

    [Fact]
    public async Task ReadAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            this._reader.ReadAsync(ClientId, Scope, Project, Repository, PullRequest, 1, 20, cancellation.Token));
    }

    private Task<PrReviewViewData> ReadAsync(int page = 1, int pageSize = 20) =>
        this._reader.ReadAsync(ClientId, Scope, Project, Repository, PullRequest, page, pageSize);

    private static ReviewJob CreateJob(
        Guid? clientId = null, string scope = Scope, string project = Project,
        string repository = Repository, int pullRequest = PullRequest, DateTimeOffset? submittedAt = null) =>
        new(Guid.NewGuid(), clientId ?? ClientId, scope, project, repository, pullRequest, 1)
            { SubmittedAt = submittedAt ?? DateTimeOffset.UtcNow };

    private static ReviewJobProtocol AddProtocol(ReviewJob job, long? input = null, long? output = null, int attempt = 1)
    {
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(), JobId = job.Id, AttemptNumber = attempt, StartedAt = DateTimeOffset.UtcNow,
            TotalInputTokens = input, TotalOutputTokens = output,
        };
        job.Protocols.Add(protocol);
        return protocol;
    }

    private static ProtocolEvent AddEvent(
        ReviewJobProtocol protocol, string name, string? input,
        DateTimeOffset? occurredAt = null)
    {
        var ev = new ProtocolEvent
        {
            Id = Guid.NewGuid(), ProtocolId = protocol.Id, Name = name, InputTextSample = input,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        };
        protocol.Events.Add(ev);
        return ev;
    }

    private static ThreadPassJob CreatePass(
        int index, DateTimeOffset createdAt, Guid? clientId = null,
        string scope = Scope, string project = Project, string repository = Repository, int pullRequest = PullRequest) =>
        new(Guid.NewGuid(), clientId ?? ClientId, scope, project, repository, pullRequest, index + 1, index.ToString(), index.ToString())
            { Status = ThreadPassJobStatus.Completed, CreatedAt = createdAt };
}
