// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsCountRepositoryTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = WindowEnd.AddDays(-7);
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    // The account count is a point-in-time count of who can sign in, so a disabled account is excluded.
    [Fact]
    public async Task DisabledAccounts_AreNotCountedAsActive()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: false);
        AddUser(db, active: true);
        AddUser(db, active: true);
        AddUser(db, active: false);
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(2, counts.ActiveUserAccounts);
    }

    // The count is installation-wide. The payload has no field for a per-client breakdown.
    [Fact]
    public async Task ReviewedPullRequests_AreCountedAcrossEveryClient()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: false);
        AddCompletedJob(db, ClientId, pullRequestId: 1, completedAt: WindowEnd.AddDays(-1));
        AddCompletedJob(db, OtherClientId, pullRequestId: 2, completedAt: WindowEnd.AddDays(-2));
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(2, counts.PullRequestsReviewed);
    }

    // A pull request reviewed three times as it was pushed to counts once. Counting jobs would report a team
    // that pushes often as a larger installation.
    [Fact]
    public async Task RepeatedReviewsOfOnePullRequest_CountOnce()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: false);
        AddCompletedJob(db, ClientId, pullRequestId: 7, completedAt: WindowEnd.AddDays(-3));
        AddCompletedJob(db, ClientId, pullRequestId: 7, completedAt: WindowEnd.AddDays(-2));
        AddCompletedJob(db, ClientId, pullRequestId: 7, completedAt: WindowEnd.AddDays(-1));
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(1, counts.PullRequestsReviewed);
    }

    [Fact]
    public async Task JobsThatFailedOrFellOutsideTheWindow_AreNotCounted()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: false);
        AddCompletedJob(db, ClientId, pullRequestId: 1, completedAt: WindowEnd.AddDays(-30));
        AddCompletedJob(db, ClientId, pullRequestId: 2, completedAt: WindowEnd.AddDays(-1), status: JobStatus.Failed);
        AddCompletedJob(db, ClientId, pullRequestId: 3, completedAt: WindowEnd.AddDays(-1));
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(1, counts.PullRequestsReviewed);
    }

    [Fact]
    public async Task FindingsRaised_CountsWhatWasPostedInsideTheWindow()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: false);
        AddPostedFinding(db, WindowEnd.AddDays(-1));
        AddPostedFinding(db, WindowEnd.AddDays(-2));
        AddPostedFinding(db, WindowEnd.AddDays(-40));
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(2, counts.FindingsRaised);
    }

    // An installation that measures no outcomes reports them as unknown rather than zero. A zero would read
    // as "nothing was accepted" and would lower the fleet-wide ratio with installations that never measured
    // it.
    [Fact]
    public async Task AnInstallationThatMeasuresNoOutcomes_ReportsThemAsUnknown()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: false);
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Null(counts.FindingsAccepted);
        Assert.Null(counts.FindingsDismissed);
    }

    // Accepted covers both ways an author can agree: fixing the finding, and acknowledging it without a change
    // in this pull request. A false positive is neither accepted nor dismissed.
    [Fact]
    public async Task OutcomesAreSplitIntoAcceptedAndDismissed()
    {
        await using var db = CreateContext();
        SeedClient(db, collectsOutcomes: true);
        AddDecidedFinding(db, WindowEnd.AddDays(-1), CodeInsightDisposition.Addressed);
        AddDecidedFinding(db, WindowEnd.AddDays(-1), CodeInsightDisposition.Acknowledged);
        AddDecidedFinding(db, WindowEnd.AddDays(-2), CodeInsightDisposition.Dismissed);
        AddDecidedFinding(db, WindowEnd.AddDays(-2), CodeInsightDisposition.FalsePositive);
        AddDecidedFinding(db, WindowEnd.AddDays(-2), CodeInsightDisposition.Discussed);
        AddDecidedFinding(db, WindowEnd.AddDays(-40), CodeInsightDisposition.Addressed);
        await db.SaveChangesAsync();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(2, counts.FindingsAccepted);
        Assert.Equal(1, counts.FindingsDismissed);
    }

    private static void SeedClient(MeisterProPRDbContext db, bool collectsOutcomes)
    {
        db.Clients.Add(
            new ClientRecord
            {
                Id = ClientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Usage statistics client",
                IsActive = true,
                CreatedAt = WindowStart,
                CodeInsightsCollectionEnabled = collectsOutcomes,
            });
    }

    private static void AddUser(MeisterProPRDbContext db, bool active)
    {
        db.AppUsers.Add(
            new AppUserRecord
            {
                Id = Guid.NewGuid(),
                Username = $"user-{Guid.NewGuid():N}",
                PasswordHash = "hash",
                GlobalRole = AppUserRole.User,
                IsActive = active,
                CreatedAt = WindowStart,
            });
    }

    private static void AddCompletedJob(
        MeisterProPRDbContext db,
        Guid clientId,
        int pullRequestId,
        DateTimeOffset completedAt,
        JobStatus status = JobStatus.Completed)
    {
        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            "https://dev.azure.com/test",
            "test-project",
            "test-repo",
            pullRequestId,
            1)
        {
            Status = status,
            CompletedAt = completedAt,
        };

        db.ReviewJobs.Add(job);
    }

    private static void AddPostedFinding(MeisterProPRDbContext db, DateTimeOffset createdAt)
    {
        db.PostedFindingRecords.Add(
            new PostedFindingRecord
            {
                Id = Guid.NewGuid(),
                ClientId = ClientId,
                RepositoryId = "test-repo",
                PullRequestId = 1,
                ProviderThreadId = Guid.NewGuid().ToString("N"),
                ReviewJobId = Guid.NewGuid(),
                IterationId = 1,
                FindingMessage = "A finding",
                Severity = CommentSeverity.Warning,
                EmbeddingVector = [],
                CreatedAt = createdAt,
            });
    }

    private static void AddDecidedFinding(
        MeisterProPRDbContext db,
        DateTimeOffset observedAt,
        CodeInsightDisposition disposition)
    {
        var pullRequestId = Guid.NewGuid();
        db.CodeInsightPullRequests.Add(
            new CodeInsightPullRequest
            {
                Id = pullRequestId,
                ClientId = ClientId,
                RepositoryId = "test-repo",
                PullRequestId = Random.Shared.NextInt64(1, int.MaxValue),
                PullRequestState = "active",
                LatestRevisionKey = "rev-1",
                LastActivityAt = observedAt,
                CreatedAt = observedAt,
                UpdatedAt = observedAt,
            });

        var findingId = Guid.NewGuid();
        db.CodeInsightFindings.Add(
            new CodeInsightFinding
            {
                Id = findingId,
                CodeInsightPullRequestId = pullRequestId,
                JobId = Guid.NewGuid(),
                RevisionKey = "rev-1",
                Ordinal = 1,
                Severity = CommentSeverity.Warning,
                EncryptedMessage = "encrypted",
                FindingChainId = Guid.NewGuid(),
                ObservedAt = observedAt,
                CreatedAt = observedAt,
            });

        db.CodeInsightFindingDispositions.Add(
            new CodeInsightFindingDisposition
            {
                Id = Guid.NewGuid(),
                CodeInsightFindingId = findingId,
                Disposition = disposition,
                DecidedAt = observedAt,
            });
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_UsageStatisticsCounts_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

/// <summary>
///     The counters against a real database.
///     <para>
///         The in-memory provider executes LINQ over objects and translates nothing, so it cannot show whether
///         the distinct-over-a-projection and the grouped join translate to SQL. These are the only queries the
///         feature emits, so they are covered against a real server.
///     </para>
/// </summary>
[Collection("PostgresIntegration")]
public sealed class UsageStatisticsCountSourcePostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = WindowEnd.AddDays(-7);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    [Fact]
    public async Task EveryCounterQuery_TranslatesAndRunsAgainstPostgres()
    {
        await using var db = this.CreatePostgresContext();

        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.True(counts.ActiveUserAccounts >= 0);
        Assert.Equal(0, counts.PullRequestsReviewed);
        Assert.Equal(0, counts.FindingsRaised);
        Assert.Null(counts.FindingsAccepted);
        Assert.Null(counts.FindingsDismissed);
    }

    [Fact]
    public async Task RepeatedReviewsOfOnePullRequest_CountOnceInPostgres()
    {
        var clientId = await this.SeedClientAsync();

        await using (var seed = this.CreatePostgresContext())
        {
            for (var iteration = 1; iteration <= 3; iteration++)
            {
                seed.ReviewJobs.Add(
                    new ReviewJob(
                        Guid.NewGuid(),
                        clientId,
                        "https://dev.azure.com/test",
                        "test-project",
                        "test-repo",
                        42,
                        iteration)
                    {
                        Status = JobStatus.Completed,
                        CompletedAt = WindowEnd.AddDays(-1),
                    });
            }

            await seed.SaveChangesAsync();
        }

        await using var db = this.CreatePostgresContext();
        var counts = await new UsageStatisticsCountRepository(db).CountAsync(WindowStart, WindowEnd);

        Assert.Equal(1, counts.PullRequestsReviewed);
    }

    private async Task<Guid> SeedClientAsync()
    {
        var clientId = Guid.NewGuid();

        await using var db = this.CreatePostgresContext();
        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Usage statistics client",
                IsActive = true,
                CreatedAt = WindowStart,
            });
        await db.SaveChangesAsync();

        return clientId;
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreatePostgresContext();
        await db.ReviewJobs.ExecuteDeleteAsync();
        await db.PostedFindingRecords.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
