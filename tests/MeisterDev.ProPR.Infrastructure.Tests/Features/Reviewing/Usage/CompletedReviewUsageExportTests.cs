using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Usage;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.TestSupport;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Usage;

[Collection("PostgresIntegration3")]
public sealed class CompletedReviewUsageExportTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task ExportsOnlyFinalizedCompletedJobsForOwnedClientAndReplaysStableFacts()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var db = new MeisterProPRDbContext(options);
        var tenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        var completed = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/test", "p", "r", 101, 1)
        {
            Status = JobStatus.Completed,
            ProcessingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-11),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExecutionDurationMilliseconds = 1200,
        };
        var foreign = new ReviewJob(Guid.NewGuid(), otherClientId, "https://dev.azure.com/test", "p", "r", 102, 1)
        {
            Status = JobStatus.Completed,
            ProcessingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-11),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        var unmeasuredHistorical = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/test", "p", "r", 104, 1)
        {
            Status = JobStatus.Completed,
            ProcessingStartedAt = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ExecutionDurationMilliseconds = null,
        };
        var later = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/test", "p", "r", 106, 1)
        {
            Status = JobStatus.Completed,
            ProcessingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            ExecutionDurationMilliseconds = 250,
            UsageFinalizedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = $"usage-{tenantId:N}", DisplayName = "Usage tenant" });
            db.Tenants.Add(new TenantRecord { Id = foreignTenantId, Slug = $"usage-{foreignTenantId:N}", DisplayName = "Other usage tenant" });
            db.Clients.Add(new ClientRecord { Id = clientId, TenantId = tenantId, DisplayName = "usage test" });
            db.Clients.Add(new ClientRecord { Id = otherClientId, TenantId = foreignTenantId, DisplayName = "other usage test" });
            db.ReviewJobs.AddRange(completed, foreign, unmeasuredHistorical);
            await db.SaveChangesAsync();

            var export = new CompletedReviewUsageExport(db);
            var first = await export.GetPageAsync(tenantId, clientId, null, 10, default);
            var replay = await export.GetPageAsync(tenantId, clientId, null, 10, default);
            Assert.Equal(completed.Id, Assert.Single(first.Items).JobId);
            Assert.Equal(1200, first.Items[0].ExecutionDurationMilliseconds);
            Assert.Null(first.Items[0].EstimatedCostUsd);
            Assert.Equal(first.Items[0].Sequence, replay.Items[0].Sequence);
            Assert.Empty((await export.GetPageAsync(tenantId, otherClientId, null, 10, default)).Items);
            Assert.Empty((await export.GetPageAsync(tenantId, clientId, first.Items[0].Sequence, 10, default)).Items);

            db.ReviewJobs.Add(later);
            await db.SaveChangesAsync();
            var changedReplay = await export.GetPageAsync(tenantId, clientId, null, 10, default);
            Assert.Equal([completed.Id, later.Id], changedReplay.Items.Select(item => item.JobId));
            Assert.Equal(later.Id, Assert.Single((await export.GetPageAsync(tenantId, clientId, first.Items[0].Sequence, 10, default)).Items).JobId);
        }
        finally
        {
            await using var cleanup = new MeisterProPRDbContext(options);
            await cleanup.CompletedReviewUsageSnapshots.Where(x => x.JobId == completed.Id || x.JobId == foreign.Id || x.JobId == later.Id)
                .ExecuteDeleteAsync();
            await cleanup.ReviewJobs.Where(x => x.Id == completed.Id || x.Id == foreign.Id || x.Id == unmeasuredHistorical.Id || x.Id == later.Id)
                .ExecuteDeleteAsync();
            await cleanup.Clients.Where(x => x.Id == clientId || x.Id == otherClientId).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenantId || x.Id == foreignTenantId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ReconcilesEveryStaleJobWhenTheCandidateCountExceedsOneBatch()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var db = new MeisterProPRDbContext(options);
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        const int jobCount = 501;

        try
        {
            db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = $"usage-{tenantId:N}", DisplayName = "Usage tenant" });
            db.Clients.Add(new ClientRecord { Id = clientId, TenantId = tenantId, DisplayName = "usage test" });
            var completedAt = DateTimeOffset.UtcNow.AddHours(-2);
            db.ReviewJobs.AddRange(
                Enumerable.Range(0, jobCount).Select(index =>
                    new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/test", "p", "r", 2000 + index, 1)
                    {
                        Status = JobStatus.Completed,
                        ProcessingStartedAt = completedAt.AddSeconds(index - 60),
                        CompletedAt = completedAt.AddSeconds(index),
                        ExecutionDurationMilliseconds = 1000,
                    }));
            await db.SaveChangesAsync();

            var export = new CompletedReviewUsageExport(db);
            var seen = new HashSet<Guid>();
            long? cursor = null;
            for (var pageNumber = 0; pageNumber < 7; pageNumber++)
            {
                var page = await export.GetPageAsync(tenantId, clientId, cursor, 100, default);
                foreach (var item in page.Items)
                {
                    Assert.True(seen.Add(item.JobId));
                }

                cursor = page.NextCursor;
                if (cursor is null)
                {
                    break;
                }
            }

            Assert.Equal(jobCount, seen.Count);
            Assert.Null(cursor);
        }
        finally
        {
            await using var cleanup = new MeisterProPRDbContext(options);
            await cleanup.CompletedReviewUsageSnapshots.Where(x => x.ClientId == clientId).ExecuteDeleteAsync();
            await cleanup.ReviewJobs.Where(x => x.ClientId == clientId).ExecuteDeleteAsync();
            await cleanup.Clients.Where(x => x.Id == clientId).ExecuteDeleteAsync();
            await cleanup.Tenants.Where(x => x.Id == tenantId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task AccumulatesProcessingIntervalsAcrossRetriesWithoutQueueWait()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var db = new MeisterProPRDbContext(options);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/test", "p", "r", 103, 1);
        try
        {
            db.ReviewJobs.Add(job);
            await db.SaveChangesAsync();
            var firstStart = DateTimeOffset.UtcNow.AddSeconds(-2);
            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Processing)
                .SetProperty(x => x.ProcessingStartedAt, firstStart));
            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Pending));
            var firstDuration = await db.ReviewJobs.AsNoTracking().Where(x => x.Id == job.Id)
                .Select(x => x.ExecutionDurationMilliseconds).SingleAsync();
            Assert.InRange(firstDuration!.Value, 1800, 4000);

            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Processing)
                .SetProperty(x => x.ProcessingStartedAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.Completed));
            var total = await db.ReviewJobs.AsNoTracking().Where(x => x.Id == job.Id)
                .Select(x => x.ExecutionDurationMilliseconds).SingleAsync();
            Assert.InRange(total!.Value - firstDuration.Value, 800, 3000);
        }
        finally
        {
            await using var cleanup = new MeisterProPRDbContext(options);
            await cleanup.ReviewJobs.Where(x => x.Id == job.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task MeasuresProcessingJobWithNullDuration()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var db = new MeisterProPRDbContext(options);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/test", "p", "r", 105, 1);
        try
        {
            db.ReviewJobs.Add(job);
            await db.SaveChangesAsync();
            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Processing)
                .SetProperty(x => x.ProcessingStartedAt, DateTimeOffset.UtcNow.AddSeconds(-1))
                .SetProperty(x => x.ExecutionDurationMilliseconds, (long?)null));

            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Completed)
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));

            var duration = await db.ReviewJobs.AsNoTracking().Where(x => x.Id == job.Id)
                .Select(x => x.ExecutionDurationMilliseconds).SingleAsync();
            Assert.NotNull(duration);
            Assert.InRange(duration.Value, 800, 3000);
        }
        finally
        {
            await using var cleanup = new MeisterProPRDbContext(options);
            await cleanup.ReviewJobs.Where(x => x.Id == job.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task StopsCountingProcessingTimeAtLeaseExpiry()
    {
        fixture.SkipIfUnavailable();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var db = new MeisterProPRDbContext(options);
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/test", "p", "r", 107, 1);
        try
        {
            db.ReviewJobs.Add(job);
            await db.SaveChangesAsync();
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Processing)
                .SetProperty(x => x.ProcessingStartedAt, now.AddSeconds(-3))
                .SetProperty(x => x.LeaseExpiresAt, now.AddSeconds(-1)));

            await db.ReviewJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatus.Completed));

            var duration = await db.ReviewJobs.AsNoTracking().Where(x => x.Id == job.Id)
                .Select(x => x.ExecutionDurationMilliseconds).SingleAsync();
            Assert.Equal(2000, duration);
        }
        finally
        {
            await using var cleanup = new MeisterProPRDbContext(options);
            await cleanup.ReviewJobs.Where(x => x.Id == job.Id).ExecuteDeleteAsync();
        }
    }
}
