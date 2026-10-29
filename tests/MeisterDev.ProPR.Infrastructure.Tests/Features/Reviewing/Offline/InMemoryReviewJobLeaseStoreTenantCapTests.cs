// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Offline;

/// <summary>
///     The cap a tenant sets on concurrent reviews, on the in-memory store. A caller cannot tell the two stores
///     apart, so the cap holds here under overlapping claims and a full tenant is refused with the same outcome.
/// </summary>
public sealed class InMemoryReviewJobLeaseStoreTenantCapTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly Guid _cappedTenant = Guid.NewGuid();
    private readonly Guid _cappedClient = Guid.NewGuid();
    private readonly Guid _otherClient = Guid.NewGuid();
    private readonly InMemoryReviewJobRepository _jobs = new();
    private readonly InMemoryReviewJobLeaseStore _store;

    public InMemoryReviewJobLeaseStoreTenantCapTests()
    {
        var otherTenant = Guid.NewGuid();
        var clients = Substitute.For<IClientRegistry>();
        clients.GetTenantIdAsync(this._cappedClient, Arg.Any<CancellationToken>()).Returns(this._cappedTenant);
        clients.GetTenantIdAsync(this._otherClient, Arg.Any<CancellationToken>()).Returns(otherTenant);
        var limits = Substitute.For<ITenantReviewExecutionLimitsStore>();
        limits.GetAsync(this._cappedTenant, Arg.Any<CancellationToken>()).Returns(new TenantReviewExecutionLimits(1, null));
        limits.GetAsync(otherTenant, Arg.Any<CancellationToken>()).Returns(TenantReviewExecutionLimits.None);

        this._store = new InMemoryReviewJobLeaseStore(
            this._jobs,
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions()),
            clients,
            limits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingClaims_RunOneJobOfATenantWithACapOfOne(bool underAnInstallationCap)
    {
        var cappedJobs = new List<ReviewJob>();
        for (var prId = 1; prId <= 6; prId++)
        {
            cappedJobs.Add(await this.AddPendingJobAsync(this._cappedClient, prId));
        }

        List<ReviewJob> jobs = [.. cappedJobs, await this.AddPendingJobAsync(this._otherClient, 101), await this.AddPendingJobAsync(this._otherClient, 102)];

        // Every claimant waits on one gate and is released together, so the claims overlap.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = jobs
            .Select((job, index) => Task.Run(async () =>
            {
                await start.Task;
                return underAnInstallationCap
                    ? (await this._store.TryClaimWithinProcessingCapAsync(job.Id, $"host-{index}", LeaseDuration, 100)).Lease
                    : await this._store.TryClaimAsync(job.Id, $"host-{index}", LeaseDuration);
            }))
            .ToList();
        start.SetResult();
        var leases = await Task.WhenAll(claims);

        Assert.Single(leases, lease => lease is not null && cappedJobs.Any(job => job.Id == lease.JobId));
        Assert.Equal(1, await this.CountProcessingAsync(this._cappedClient));
        Assert.Equal(2, await this.CountProcessingAsync(this._otherClient));
    }

    [Fact]
    public async Task AClaimForATenantAtItsCap_NamesTheTenantAndLeavesTheJobPending()
    {
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(this._cappedClient, 1)).Id, "host-a", LeaseDuration));
        var queued = await this.AddPendingJobAsync(this._cappedClient, 2);

        var claim = await this._store.TryClaimWithinProcessingCapAsync(queued.Id, "host-b", LeaseDuration, 10);

        Assert.Equal(ReviewJobCappedClaimOutcome.TenantAtCapacity, claim.Outcome);
        Assert.Equal(this._cappedTenant, claim.TenantId);
        Assert.Equal(1, claim.Cap);
        Assert.Equal(1, claim.ProcessingCount);
        Assert.Equal(JobStatus.Pending, this._jobs.GetById(queued.Id)!.Status);
    }

    [Fact]
    public async Task TheBacklogOfAFullTenant_DoesNotHideTheJobsOfOtherTenants()
    {
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(this._cappedClient, 1)).Id, "host-a", LeaseDuration));
        await this.AddPendingJobAsync(this._cappedClient, 2);
        await this.AddPendingJobAsync(this._cappedClient, 3);
        var behind = await this.AddPendingJobAsync(this._otherClient, 101);

        var candidates = await this._store.GetClaimCandidatesAsync(1);

        Assert.Equal([behind.Id], candidates.Select(job => job.Id));
    }

    // The offline harness composes the store without tenant lookups, because it has no tenants. Such a store claims
    // as it did before tenants could set a cap.
    [Fact]
    public async Task AStoreWithoutTenantLookups_ClaimsWithoutATenantCap()
    {
        var store = new InMemoryReviewJobLeaseStore(this._jobs, Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions()));
        var first = await this.AddPendingJobAsync(this._cappedClient, 1);
        var second = await this.AddPendingJobAsync(this._cappedClient, 2);

        Assert.NotNull(await store.TryClaimAsync(first.Id, "host-a", LeaseDuration));
        Assert.NotNull(await store.TryClaimAsync(second.Id, "host-b", LeaseDuration));
    }

    private async Task<int> CountProcessingAsync(Guid clientId)
    {
        return (await this._jobs.GetProcessingJobsAsync()).Count(job => job.ClientId == clientId);
    }

    private async Task<ReviewJob> AddPendingJobAsync(Guid clientId, int prId)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", prId, 1);
        await this._jobs.AddAsync(job);
        return job;
    }
}
