// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.History;

namespace MeisterDev.ProPR.CodeInsights.Tests.History;

/// <summary>
///     Coverage answers one question: how much of what has already been reviewed does the collection know about.
///     A reader who cannot tell "the reviewer found little" from "collection was off then" cannot use any other
///     number on the surface, so these tests pin the comparison rather than the presentation.
/// </summary>
public sealed class CodeInsightHistoryCoverageTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 31);
    private static readonly DateTimeOffset InWindow = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly MeisterProPRDbContext _dbContext;
    private readonly ICodeInsightsCollectionGate _gate;
    private readonly CodeInsightHistoryReader _reader;

    public CodeInsightHistoryCoverageTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightHistoryCoverageTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        this._gate = Substitute.For<ICodeInsightsCollectionGate>();
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        this._reader = new CodeInsightHistoryReader(this._dbContext, this._gate);
    }

    public void Dispose() => this._dbContext.Dispose();

    [Fact]
    public async Task Counts_what_the_reviews_produced_against_what_the_collection_holds()
    {
        // Two jobs on one repository produced five findings between them; the collection holds two of them,
        // which is the shape of an installation that switched collection on halfway through the window.
        var firstJob = await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 41, producedFindings: 3);
        await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 42, producedFindings: 2);
        await this.SeedCollectedAsync(ClientA, "repo-1", pullRequestId: 41, jobId: firstJob, findings: 2);

        var coverage = await this.ReadAsync(ClientA);

        var row = Assert.Single(coverage.Rows);
        Assert.Equal(2, row.ReviewJobs);
        Assert.Equal(1, row.JobsCollected);
        Assert.Equal(5, row.ProducedFindings);
        Assert.Equal(2, row.CollectedFindings);
        Assert.Equal(2, row.PullRequests);
        Assert.Equal(5, coverage.ProducedFindings);
        Assert.Equal(2, coverage.CollectedFindings);
    }

    [Fact]
    public async Task Reports_retained_pull_requests_separately_because_outcomes_are_only_recoverable_there()
    {
        // A review's own result carries the findings; how a human resolved them lives on the threads, and those
        // exist only where retention is on. An importer can rebuild findings for both and outcomes for one.
        await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 41, producedFindings: 2);
        await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 42, producedFindings: 2);
        await this.SeedRetainedAsync(ClientA, "repo-1", pullRequestId: 41, threads: 4);

        var coverage = await this.ReadAsync(ClientA);

        var row = Assert.Single(coverage.Rows);
        Assert.Equal(2, row.PullRequests);
        Assert.Equal(1, row.PullRequestsRetained);
        Assert.Equal(4, row.RetainedThreads);
        Assert.Equal(1, coverage.PullRequestsRetained);
    }

    [Fact]
    public async Task Ranks_the_least_covered_repository_first()
    {
        var covered = await this.SeedJobAsync(ClientA, "covered", pullRequestId: 1, producedFindings: 4);
        await this.SeedCollectedAsync(ClientA, "covered", pullRequestId: 1, jobId: covered, findings: 4);
        await this.SeedJobAsync(ClientA, "blind", pullRequestId: 2, producedFindings: 9);

        var coverage = await this.ReadAsync(ClientA);

        // The row worth acting on is the one furthest behind, not the busiest.
        Assert.Equal("blind", coverage.Rows[0].RepositoryId);
        Assert.Equal("covered", coverage.Rows[1].RepositoryId);
    }

    [Fact]
    public async Task Says_how_many_clients_have_collection_switched_off()
    {
        // Their absence from every other number is a setting rather than missing data, and an import cannot
        // touch them: the gate is consulted on every collection path.
        await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 41, producedFindings: 2);
        this._gate.IsCollectionEnabledAsync(ClientA, Arg.Any<CancellationToken>()).Returns(false);

        var coverage = await this.ReadAsync(ClientA);

        Assert.Equal(1, coverage.ClientsWithCollectionOff);
        Assert.Equal(2, coverage.ProducedFindings);
    }

    [Fact]
    public async Task Reads_nothing_about_a_client_the_caller_cannot_see()
    {
        await this.SeedJobAsync(ClientB, "other-repo", pullRequestId: 7, producedFindings: 5);

        var coverage = await this.ReadAsync(ClientA);

        Assert.Empty(coverage.Rows);
        Assert.Equal(0, coverage.ProducedFindings);
    }

    [Fact]
    public async Task Ignores_reviews_outside_the_window_and_reviews_that_never_completed()
    {
        await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 41, producedFindings: 3);
        await this.SeedJobAsync(
            ClientA,
            "repo-1",
            pullRequestId: 42,
            producedFindings: 4,
            submittedAt: new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));
        await this.SeedJobAsync(ClientA, "repo-1", pullRequestId: 43, producedFindings: 8, status: JobStatus.Failed);

        var coverage = await this.ReadAsync(ClientA);

        var row = Assert.Single(coverage.Rows);
        Assert.Equal(1, row.ReviewJobs);
        Assert.Equal(3, row.ProducedFindings);
    }

    private Task<CodeInsightHistoryCoverage> ReadAsync(Guid clientId) =>
        this._reader.GetCoverageAsync(new CodeInsightHistoryCoverageQuery([clientId], From, To));

    [Fact]
    public async Task Names_a_repository_the_collection_knows_nothing_about()
    {
        // The rows most worth reading here are the ones with nothing collected, and those have no collected name
        // by definition. Falling back to the identifier left the table reading "4" and "8" at a reader.
        await this.SeedJobAsync(ClientA, "42", pullRequestId: 41, producedFindings: 3, repositoryName: "payments-api");

        var coverage = await this.ReadAsync(ClientA);

        var row = Assert.Single(coverage.Rows);
        Assert.Equal("42", row.RepositoryId);
        Assert.Equal("payments-api", row.RepositoryName);
        Assert.Equal(0, row.CollectedFindings);
    }

    [Fact]
    public async Task Prefers_the_collected_name_over_the_one_a_review_recorded()
    {
        // The collected name is refreshed on every pull request, so a renamed repository settles on its current
        // name instead of on whatever it was called when a review last ran.
        var jobId = await this.SeedJobAsync(
            ClientA,
            "42",
            pullRequestId: 41,
            producedFindings: 3,
            repositoryName: "payments-api");
        await this.SeedCollectedAsync(
            ClientA,
            "42",
            pullRequestId: 41,
            jobId: jobId,
            findings: 2,
            repositoryName: "billing-api");

        var coverage = await this.ReadAsync(ClientA);

        Assert.Equal("billing-api", Assert.Single(coverage.Rows).RepositoryName);
    }

    [Fact]
    public async Task A_repository_no_source_has_named_keeps_its_identifier()
    {
        // Honest rather than blank: an unnamed row still says which repository it is.
        await this.SeedJobAsync(ClientA, "42", pullRequestId: 41, producedFindings: 1);

        var coverage = await this.ReadAsync(ClientA);

        var row = Assert.Single(coverage.Rows);
        Assert.Equal("42", row.RepositoryId);
        Assert.Null(row.RepositoryName);
    }

    private async Task<Guid> SeedJobAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int producedFindings,
        DateTimeOffset? submittedAt = null,
        JobStatus status = JobStatus.Completed,
        string? repositoryName = null)
    {
        var jobId = Guid.NewGuid();
        var job = new ReviewJob(jobId, clientId, "https://dev.azure.com/org", "project", repositoryId, pullRequestId, 1)
        {
            SubmittedAt = submittedAt ?? InWindow,
            Status = status,
            CompletedAt = submittedAt ?? InWindow,
        };
        if (repositoryName is not null)
        {
            job.SetPrContext(null, repositoryName, null, null);
        }

        this._dbContext.ReviewJobs.Add(job);

        var result = new ReviewFileResult(jobId, "src/Service.cs");
        result.MarkCompleted(
            "summary",
            Enumerable
                .Range(0, producedFindings)
                .Select(index => new ReviewComment(
                    "src/Service.cs",
                    index + 1,
                    CommentSeverity.Warning,
                    $"finding {index}"))
                .ToList());
        this._dbContext.ReviewFileResults.Add(result);

        await this._dbContext.SaveChangesAsync();
        return jobId;
    }

    private async Task SeedCollectedAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        Guid jobId,
        int findings,
        string? repositoryName = null)
    {
        var aggregate = new CodeInsightPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            RepositoryId = repositoryId,
            RepositoryName = repositoryName,
            PullRequestId = pullRequestId,
            PullRequestState = "Active",
            LatestRevisionKey = "rev-1",
            LastActivityAt = InWindow,
            CreatedAt = InWindow,
            UpdatedAt = InWindow,
        };
        this._dbContext.CodeInsightPullRequests.Add(aggregate);

        for (var ordinal = 0; ordinal < findings; ordinal++)
        {
            this._dbContext.CodeInsightFindings.Add(
                new CodeInsightFinding
                {
                    Id = Guid.NewGuid(),
                    CodeInsightPullRequestId = aggregate.Id,
                    JobId = jobId,
                    RevisionKey = "rev-1",
                    Ordinal = ordinal,
                    FindingChainId = Guid.NewGuid(),
                    FilePath = "src/Service.cs",
                    LineNumber = ordinal + 1,
                    Severity = CommentSeverity.Warning,
                    EncryptedMessage = "cipher",
                    ObservedAt = InWindow,
                });
        }

        await this._dbContext.SaveChangesAsync();
    }

    private async Task SeedRetainedAsync(Guid clientId, string repositoryId, long pullRequestId, int threads)
    {
        var retained = new RetainedPullRequest
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ConnectionId = Guid.NewGuid(),
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            PrState = "Active",
            LastActivityAt = InWindow,
            CreatedAt = InWindow,
            UpdatedAt = InWindow,
        };

        for (var index = 0; index < threads; index++)
        {
            retained.Threads.Add(
                new RetainedThread
                {
                    Id = Guid.NewGuid(),
                    RetainedPullRequestId = retained.Id,
                    ThreadId = $"thread-{index}",
                    Status = "Active",
                    UpdatedAt = InWindow,
                });
        }

        this._dbContext.RetainedPullRequests.Add(retained);
        await this._dbContext.SaveChangesAsync();
    }
}
