// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Budgeting;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Budgeting;

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
        await this._dbContext.ThreadPassJobs.ExecuteDeleteAsync();
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

        var baseline = await this._accumulator.GetBaselineAsync(ReviewSpendSubject.For(current), asOf);

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

        var baseline = await this._accumulator.GetBaselineAsync(ReviewSpendSubject.For(current), asOf);

        Assert.Equal(6.00m, baseline.ClientMonthToDate.KnownUsd);
        Assert.True(baseline.ClientMonthToDate.IsApproximate);
        Assert.Equal(5.00m, baseline.Increment.KnownUsd);
        Assert.True(baseline.Increment.IsApproximate);
        Assert.True(baseline.PullRequest.IsApproximate);
    }

    [Fact]
    public async Task GetBaselineAsync_ForAReviewJob_CountsWhatTheThreadPassesSpentOnTheSamePullRequest()
    {
        var asOf = new DateOnly(2026, 8, 3);

        var current = MakeJob(this._clientId, prId: 1, iterationId: 5);
        await this.AddJobWithCostAsync(current, costUsd: null);
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 5), 3.00m);

        // Two passes over the same pull request: one in this increment, one in an earlier one.
        await this.AddThreadPassWithCostAsync(MakeThreadPass(this._clientId, prId: 1, iterationId: 5), 1.50m);
        await this.AddThreadPassWithCostAsync(MakeThreadPass(this._clientId, prId: 1, iterationId: 4), 0.25m);
        // A pass over a different pull request must not count.
        await this.AddThreadPassWithCostAsync(MakeThreadPass(this._clientId, prId: 2, iterationId: 1), 99.00m);

        var baseline = await this._accumulator.GetBaselineAsync(ReviewSpendSubject.For(current), asOf);

        Assert.Equal(4.75m, baseline.PullRequest.KnownUsd);
        Assert.False(baseline.PullRequest.IsApproximate);
        Assert.Equal(4.50m, baseline.Increment.KnownUsd);
        Assert.False(baseline.Increment.IsApproximate);
    }

    [Fact]
    public async Task GetBaselineAsync_ForAThreadPass_CountsTheReviewJobOnceAndExcludesItsOwnRow()
    {
        var asOf = new DateOnly(2026, 8, 3);

        var current = MakeThreadPass(this._clientId, prId: 1, iterationId: 5);
        await this.AddThreadPassWithCostAsync(current, 7.00m);
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 5), 2.00m);
        await this.AddThreadPassWithCostAsync(MakeThreadPass(this._clientId, prId: 1, iterationId: 5), 0.50m);

        var baseline = await this._accumulator.GetBaselineAsync(ReviewSpendSubject.For(current), asOf);

        // The asking pass's own 7.00 is excluded; the review job and the sibling pass are each counted once.
        Assert.Equal(2.50m, baseline.PullRequest.KnownUsd);
        Assert.Equal(2.50m, baseline.Increment.KnownUsd);
    }

    [Fact]
    public async Task GetBaselineAsync_ThreadPassThatHasSpentNothingYet_LeavesTheScopeExact()
    {
        var asOf = new DateOnly(2026, 8, 3);

        var current = MakeJob(this._clientId, prId: 1, iterationId: 5);
        await this.AddJobWithCostAsync(current, costUsd: null);
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 5), 3.00m);
        // A queued pass reports no cost because it has not run, which is silence rather than unpriced spend.
        await this.AddThreadPassWithCostAsync(MakeThreadPass(this._clientId, prId: 1, iterationId: 5), costUsd: null);

        var baseline = await this._accumulator.GetBaselineAsync(ReviewSpendSubject.For(current), asOf);

        Assert.Equal(3.00m, baseline.Increment.KnownUsd);
        Assert.False(baseline.Increment.IsApproximate);
    }

    [Fact]
    public async Task GetBaselineAsync_ThreadPassWithUnpricedSpend_FlagsTheScopeApproximate()
    {
        var asOf = new DateOnly(2026, 8, 3);

        var current = MakeJob(this._clientId, prId: 1, iterationId: 5);
        await this.AddJobWithCostAsync(current, costUsd: null);
        await this.AddJobWithCostAsync(MakeJob(this._clientId, prId: 1, iterationId: 5), 3.00m);
        await this.AddThreadPassWithCostAsync(
            MakeThreadPass(this._clientId, prId: 1, iterationId: 5),
            costUsd: null,
            approximate: true);

        var baseline = await this._accumulator.GetBaselineAsync(ReviewSpendSubject.For(current), asOf);

        Assert.Equal(3.00m, baseline.Increment.KnownUsd);
        Assert.True(baseline.Increment.IsApproximate);
    }

    private static ThreadPassJob MakeThreadPass(
        Guid clientId,
        int prId,
        int iterationId,
        string org = "https://dev.azure.com/org",
        string project = "proj",
        string repo = "repo")
    {
        return new ThreadPassJob(
            Guid.NewGuid(),
            clientId,
            org,
            project,
            repo,
            prId,
            iterationId,
            iterationId.ToString(CultureInfo.InvariantCulture),
            $"{iterationId}|{Guid.NewGuid()}");
    }

    private async Task AddThreadPassWithCostAsync(ThreadPassJob pass, decimal? costUsd, bool approximate = false)
    {
        // Completed, because a pass that recorded spend has run and only one pass may be in flight over a
        // pull request at a time; a queued row here would be a state the database refuses.
        pass.Status = ThreadPassJobStatus.Completed;
        pass.CompletedAt = DateTimeOffset.UtcNow;
        this._dbContext.ThreadPassJobs.Add(pass);
        this._dbContext.Entry(pass).Property(nameof(ThreadPassJob.TotalEstimatedCostUsd)).CurrentValue = costUsd;
        this._dbContext.Entry(pass).Property(nameof(ThreadPassJob.CostIsApproximate)).CurrentValue = approximate;
        await this._dbContext.SaveChangesAsync();
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
