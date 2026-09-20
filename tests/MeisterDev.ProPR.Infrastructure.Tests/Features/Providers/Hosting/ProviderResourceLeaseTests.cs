// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     The named lease against a real PostgreSQL instance.
/// </summary>
/// <remarks>
///     The lease is stated across the installation rather than within one process, so it has to be recorded and
///     the arbitration has to be the database's. A double held in memory would agree with itself on one replica
///     and let two replicas both bind the same port.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class ProviderResourceLeaseTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a caller in these tests waits before reporting that the resource is held.</summary>
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(200);

    private ProviderHostPrimitiveHarness _harness = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._harness = ProviderHostPrimitiveHarness.Create(fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (fixture.IsAvailable)
        {
            await this._harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task AResourceIsTakenForOneConnectionAndGivenBackWhenTheLeaseIsDisposed()
    {
        var binding = await this._harness.SeedConnectionAsync("Takes the port");
        var resource = NewResource();

        var outcome = await this._harness.Leases(binding).AcquireAsync(resource, ShortWait);

        Assert.True(outcome.Acquired);
        Assert.Equal(resource, outcome.Lease!.ResourceName);

        await outcome.Lease.DisposeAsync();

        var again = await this._harness.Leases(binding).AcquireAsync(resource, ShortWait);
        Assert.True(again.Acquired);
        await again.Lease!.DisposeAsync();
    }

    // A port is held by one process and one socket at a time, so the second connection is told which connection
    // holds it. Failing on the bind instead would give the operator an operating-system error naming nothing
    // they can act on.
    [Fact]
    public async Task ASecondConnectionOfTheSameFamilyIsRefusedAndToldWhichConnectionHoldsTheResource()
    {
        var first = await this._harness.SeedConnectionAsync("Production OpenAI");
        var second = await this._harness.SeedConnectionAsync("Staging OpenAI");
        var resource = NewResource();

        await using var held = (await this._harness.Leases(first).AcquireAsync(resource, ShortWait)).Lease!;

        var refused = await this._harness.Leases(second).AcquireAsync(resource, ShortWait);

        Assert.False(refused.Acquired);
        Assert.Equal("Production OpenAI", refused.HeldByConnection);
    }

    [Fact]
    public async Task TwoFamiliesHoldLeasesOnOneResourceNameWithoutContending()
    {
        var first = await this._harness.SeedConnectionAsync("First family", "example/first");
        var second = await this._harness.SeedConnectionAsync("Second family", "example/second");
        var resource = NewResource();

        await using var one = (await this._harness.Leases(first).AcquireAsync(resource, ShortWait)).Lease!;
        var other = await this._harness.Leases(second).AcquireAsync(resource, ShortWait);

        Assert.NotNull(other.Lease);
        await other.Lease.DisposeAsync();
    }

    // A replica that died holding a resource must not hold it for good, so the lease lapses on its own expiry
    // whether or not anything released it.
    [Fact]
    public async Task ALeaseWhoseHolderNeverReleasesItLapsesAtItsExpiryAndTheResourceBecomesAvailable()
    {
        var first = await this._harness.SeedConnectionAsync("Died holding it");
        var second = await this._harness.SeedConnectionAsync("Waiting for it");
        var resource = NewResource();

        var held = await this._harness.Leases(first).AcquireAsync(resource, ShortWait);
        Assert.True(held.Acquired);

        // Stands in for time passing: the expiry is a column, and what these tests are about is that the lease
        // lapses on it rather than on anything the holder does.
        await this.ExpireAsync(first.AddInKey, resource);

        var taken = await this._harness.Leases(second).AcquireAsync(resource, ShortWait);

        Assert.True(taken.Acquired);
        await taken.Lease!.DisposeAsync();
    }

    // The defect this rules out: a read followed by an insert, where two connections both find the resource
    // free and the second takes it from the first.
    [Fact]
    public async Task TwoConcurrentAttemptsOnOneResourceProduceExactlyOneHolder()
    {
        const int claimants = 6;

        var bindings = new List<ProviderAddInBinding>();
        for (var index = 0; index < claimants; index++)
        {
            bindings.Add(await this._harness.SeedConnectionAsync($"Claimant {index}"));
        }

        var resource = NewResource();
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = bindings.Select(binding => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == claimants)
            {
                allArrived.SetResult();
            }

            await release.Task;

            // No wait at all, so a loser reports that the resource is held rather than retrying until the
            // winner gives it back and turning this into a queue.
            return await this._harness.Leases(binding).AcquireAsync(resource, TimeSpan.Zero);
        })).ToList();

        await allArrived.Task.WaitAsync(TestTimeout);
        release.SetResult();
        var outcomes = await Task.WhenAll(attempts).WaitAsync(TestTimeout);

        Assert.Single(outcomes, outcome => outcome.Acquired);
        Assert.All(
            outcomes.Where(outcome => !outcome.Acquired),
            outcome => Assert.NotNull(outcome.HeldByConnection));

        await outcomes.Single(outcome => outcome.Acquired).Lease!.DisposeAsync();
    }

    // Releasing is keyed on the lease that was granted, not on the resource name. A lease that lapsed may
    // already have been taken by another connection, and releasing by name would take it away from its holder.
    [Fact]
    public async Task ReleasingALeaseThatHasAlreadyLapsedLeavesALeaseTakenSinceAlone()
    {
        var first = await this._harness.SeedConnectionAsync("Lapsed holder");
        var second = await this._harness.SeedConnectionAsync("Took it since");
        var resource = NewResource();

        var lapsed = await this._harness.Leases(first).AcquireAsync(resource, ShortWait);
        await this.ExpireAsync(first.AddInKey, resource);

        var taken = await this._harness.Leases(second).AcquireAsync(resource, ShortWait);
        Assert.True(taken.Acquired);

        await lapsed.Lease!.DisposeAsync();

        await using var db = this._harness.CreateContext();
        var holder = await db.ProviderResourceLeases
            .AsNoTracking()
            .Where(lease => lease.AddInKey == first.AddInKey && lease.ResourceName == resource)
            .Select(lease => lease.ConnectionProfileId)
            .SingleOrDefaultAsync();

        Assert.Equal(second.ConnectionProfileId, holder);

        await taken.Lease!.DisposeAsync();
    }

    // A caller's timeout is what it asked to wait, not the first retry boundary that follows it. Delaying a whole
    // interval whichever timeout was asked for makes a caller who wanted a tenth of a second wait more than twice
    // that, which is the difference between an action reporting quickly and one that looks stuck.
    [Fact]
    public async Task AWaitForAHeldResourceEndsAtTheCallersOwnTimeoutAndNotAtTheNextRetryBoundary()
    {
        var resource = NewResource();
        var timeout = TimeSpan.FromMilliseconds(100);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var holder = await this._harness.SeedConnectionAsync("Holds it");
        var waiter = await this._harness.SeedConnectionAsync("Waits for it");

        var held = await this._harness.Leases(holder).AcquireAsync(resource, TimeSpan.Zero);
        Assert.True(held.Acquired);

        var waiting = Task.Run(() => this._harness.Leases(waiter, clock).AcquireAsync(resource, timeout));

        // The first attempt has to reach the database and fail before its wait exists to be advanced past.
        await Task.Delay(TimeSpan.FromSeconds(2));
        clock.Advance(timeout);

        var outcome = await waiting.WaitAsync(TestTimeout);

        Assert.False(outcome.Acquired);
        Assert.Equal(holder.ConnectionDisplayName, outcome.HeldByConnection);

        await held.Lease!.DisposeAsync();
    }

    // The wait an add-in asks for governs retrying, and an attempt that never returns would hold the caller past
    // it however short it was. Each attempt is bounded on its own, so a database that stops answering ends the
    // call with a timeout the caller can report instead of a wait with no end.
    [Fact]
    public async Task AnAttemptThatCannotReachTheRowEndsAtTheHostsOwnBoundAndNotAtTheCallersWait()
    {
        var binding = await this._harness.SeedConnectionAsync("Waits on a held lock");
        var resource = NewResource();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // Holds the family's advisory lock for the length of this transaction, which an attempt takes
        // before it reads anything. Nothing else can make a real database stop answering on demand.
        await using var blocker = this._harness.CreateContext();
        await using var held = await blocker.Database.BeginTransactionAsync();
        await ProviderAdvisoryLock.TakeAsync(blocker, ProviderAdvisoryLock.ResourceLeases, binding.AddInKey, CancellationToken.None);

        var waiting = Task.Run(() => this._harness.Leases(binding, clock).AcquireAsync(resource, TimeSpan.Zero));

        // The attempt has to reach the lock and block on it before its bound exists to be advanced past.
        await Task.Delay(TimeSpan.FromSeconds(2));
        clock.Advance(ProviderHostLimits.LeaseAttemptTimeout + TimeSpan.FromSeconds(1));

        var failure = await Assert.ThrowsAsync<ProviderHostTimeoutException>(() => waiting.WaitAsync(TestTimeout));

        Assert.Contains(resource, failure.Message, StringComparison.Ordinal);

        await held.RollbackAsync();
    }

    /// <summary>Moves a held lease's expiry into the past, standing in for the time it would take to pass.</summary>
    /// <param name="addInKey">The family holding it.</param>
    /// <param name="resource">The resource held.</param>
    private async Task ExpireAsync(string addInKey, string resource)
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var db = this._harness.CreateContext();
        await db.ProviderResourceLeases
            .Where(lease => lease.AddInKey == addInKey && lease.ResourceName == resource)
            .ExecuteUpdateAsync(update => update.SetProperty(lease => lease.ExpiresAt, past));
    }

    /// <summary>A resource name nothing else in the shared database is using.</summary>
    private static string NewResource()
    {
        return $"listener-port-{Guid.NewGuid():N}";
    }
}
