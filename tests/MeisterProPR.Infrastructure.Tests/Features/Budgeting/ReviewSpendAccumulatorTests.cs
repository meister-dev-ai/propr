// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Budgeting;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Integration tests for <see cref="ReviewSpendAccumulator" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ReviewSpendAccumulatorTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private Guid _clientId;
    private MeisterProPRDbContext _dbContext = null!;
    private ReviewSpendAccumulator _accumulator = null!;
    private ClientTokenUsageRepository _usageRepo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Clean slate so the scope sums are deterministic (the collection runs serially).
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        await this._dbContext.ClientTokenUsageSamples.ExecuteDeleteAsync();

        this._clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Budget Accumulator Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();

        this._usageRepo = new ClientTokenUsageRepository(this._dbContext);
        this._accumulator = new ReviewSpendAccumulator(new TestDbContextFactory(options), this._usageRepo);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.ClientTokenUsageSamples.Where(s => s.ClientId == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task GetBaselineAsync_SumsEachScopeExcludingTheJobItself()
    {
        var asOf = new DateOnly(2026, 7, 19);

        // The in-flight job (PR1, iteration 5) has no cost yet and is excluded from every scope.
        var current = MakeJob(this._clientId, prId: 1, iterationId: 5);
        await this.AddJobWithCostAsync(current, costUsd: null);

        // Same increment (PR1, iteration 5) — a first attempt and a restart both count (paid work is respected).
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 5), 3.00m);
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 5), 2.00m);
        // Same PR, a different increment (iteration 4) — counts toward the PR scope only.
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 4), 7.00m);
        // A different PR and a different client must not count.
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 2, iterationId: 1), 100.00m);
        await this.AddJobWithCostAsync(MakeJob(Guid.NewGuid(), prId: 1, iterationId: 5), 500.00m);

        // Client month-to-date: 4 + 6 = 10 within July; a June sample is a prior period and must not count.
        await this._usageRepo.UpsertAsync(this._clientId, "gpt-4o", new DateOnly(2026, 7, 1), 100, 50, default, estimatedCostUsd: 4.00m);
        await this._usageRepo.UpsertAsync(this._clientId, "gpt-4o", new DateOnly(2026, 7, 19), 100, 50, default, estimatedCostUsd: 6.00m);
        await this._usageRepo.UpsertAsync(this._clientId, "gpt-4o", new DateOnly(2026, 6, 30), 100, 50, default, estimatedCostUsd: 50.00m);

        var baseline = await this._accumulator.GetBaselineAsync(current, asOf);

        Assert.Equal(10.00m, baseline.ClientMonthToDate.KnownUsd);
        Assert.False(baseline.ClientMonthToDate.IsApproximate);
        Assert.Equal(12.00m, baseline.PullRequest.KnownUsd); // 3 + 2 + 7
        Assert.False(baseline.PullRequest.IsApproximate);
        Assert.Equal(5.00m, baseline.Increment.KnownUsd); // 3 + 2 (iteration 5 only)
        Assert.False(baseline.Increment.IsApproximate);
    }

    [Fact]
    public async Task GetBaselineAsync_FlagsApproximateWhenAContributionIsUnpriced()
    {
        var asOf = new DateOnly(2026, 7, 19);

        var current = MakeJob(this._clientId, prId: 1, iterationId: 1);
        await this.AddJobWithCostAsync(current, costUsd: null);

        // One priced and one unpriced job in the same increment: the total is known-but-partial, hence approximate.
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 1), 5.00m);
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 1), costUsd: null);

        // An unpriced client sample likewise makes the client total approximate.
        await this._usageRepo.UpsertAsync(this._clientId, "gpt-4o", new DateOnly(2026, 7, 10), 100, 50, default, estimatedCostUsd: 6.00m);
        await this._usageRepo.UpsertAsync(this._clientId, "unpriced-model", new DateOnly(2026, 7, 11), 100, 50, default, estimatedCostUsd: null);

        var baseline = await this._accumulator.GetBaselineAsync(current, asOf);

        Assert.Equal(6.00m, baseline.ClientMonthToDate.KnownUsd);
        Assert.True(baseline.ClientMonthToDate.IsApproximate);
        Assert.Equal(5.00m, baseline.Increment.KnownUsd);
        Assert.True(baseline.Increment.IsApproximate);
        Assert.True(baseline.PullRequest.IsApproximate);
    }

    private static ReviewJob MakeJob(
        Guid clientId,
        int prId,
        int iterationId,
        string org = "https://dev.azure.com/org",
        string project = "proj",
        string repo = "repo")
    {
        return new ReviewJob(Guid.NewGuid(), clientId, org, project, repo, prId, iterationId);
    }

    private async Task AddJobWithCostAsync(ReviewJob job, decimal? costUsd, bool approximate = false)
    {
        this._dbContext.ReviewJobs.Add(job);
        this._dbContext.Entry(job).Property(nameof(ReviewJob.TotalEstimatedCostUsd)).CurrentValue = costUsd;
        this._dbContext.Entry(job).Property(nameof(ReviewJob.CostIsApproximate)).CurrentValue = approximate;
        await this._dbContext.SaveChangesAsync();
    }
}
