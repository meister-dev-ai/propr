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
///     The add-in keyed store against a real PostgreSQL instance.
/// </summary>
/// <remarks>
///     Single use is the property that needs a database: it holds because the claim is one conditional
///     statement, and a double that read and then wrote would let two callers both be told they won. A browser
///     delivering one callback twice is the ordinary case, not an unlikely one.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class ProviderKeyedStoreTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

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
    public async Task AnEntryIsWrittenUnderAKeyOfTheFamilysChoosingAndClaimedBackBeforeItExpires()
    {
        var binding = await this._harness.SeedConnectionAsync("Writes entries");
        var store = this._harness.Store(binding);
        var key = NewKey();

        await store.WriteAsync(
            key,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["codeVerifier"] = "a-verifier" },
            DateTimeOffset.UtcNow.AddMinutes(10));

        var claim = await store.ClaimAsync(key);

        Assert.True(claim.Claimed);
        Assert.Equal("a-verifier", claim.Values["codeVerifier"]);
    }

    // The entry is scoped to the family that wrote it, so one family cannot read the handshake of another it
    // happens to share a host with.
    [Fact]
    public async Task TwoFamiliesUsingOneEntryKeyDoNotSeeEachOthersEntry()
    {
        var first = await this._harness.SeedConnectionAsync("First family", "example/first");
        var second = await this._harness.SeedConnectionAsync("Second family", "example/second");
        var key = NewKey();

        await this._harness.Store(first).WriteAsync(
            key,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "the-first" },
            DateTimeOffset.UtcNow.AddMinutes(10));
        await this._harness.Store(second).WriteAsync(
            key,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "the-second" },
            DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.Equal("the-first", (await this._harness.Store(first).ClaimAsync(key)).Values["value"]);
        Assert.Equal("the-second", (await this._harness.Store(second).ClaimAsync(key)).Values["value"]);
    }

    [Fact]
    public async Task WritingAnEntryWithNoExpiryIsRefused()
    {
        var binding = await this._harness.SeedConnectionAsync("No expiry");
        var store = this._harness.Store(binding);

        var refusal = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => store.WriteAsync(
            NewKey(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "x" },
            default));

        Assert.Equal("expiresAt", refusal.ParamName);
    }

    // Each refusal is a different thing for an operator to do. A single "could not claim" would leave a
    // duplicate browser delivery, a flow that took too long and a state value from somewhere else
    // indistinguishable.
    [Fact]
    public async Task AClaimReportsWhichOfTheFourWaysItFailed()
    {
        var binding = await this._harness.SeedConnectionAsync("Refusals");
        var administrator = Guid.NewGuid();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = this._harness.Store(binding, administrator, clock);

        var consumed = NewKey();
        await store.WriteAsync(
            consumed,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "x" },
            clock.GetUtcNow().AddMinutes(10));
        Assert.True((await store.ClaimAsync(consumed)).Claimed);
        Assert.Equal(ProviderClaimRefusal.AlreadyConsumed, (await store.ClaimAsync(consumed)).Refusal);

        var expired = NewKey();
        await store.WriteAsync(
            expired,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "x" },
            clock.GetUtcNow().AddMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(ProviderClaimRefusal.Expired, (await store.ClaimAsync(expired)).Refusal);

        Assert.Equal(ProviderClaimRefusal.NoSuchEntry, (await store.ClaimAsync(NewKey())).Refusal);

        // Written under one administrator and claimed under another, which is a completion arriving for an
        // authorization somebody else started.
        var otherAdministrator = NewKey();
        await store.WriteAsync(
            otherAdministrator,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "x" },
            clock.GetUtcNow().AddMinutes(10));

        var claimedByAnother = await this._harness.Store(binding, Guid.NewGuid(), clock)
            .ClaimAsync(otherAdministrator);

        Assert.Equal(ProviderClaimRefusal.WrongPrincipal, claimedByAnother.Refusal);
    }

    // The defect this rules out: a read followed by a delete, where two callers both find the entry unconsumed
    // and both proceed to exchange the same authorization code.
    [Fact]
    public async Task TwoConcurrentClaimsOnOneEntryProduceExactlyOneWinner()
    {
        const int claimants = 8;

        var binding = await this._harness.SeedConnectionAsync("Contended entry");
        var key = NewKey();
        await this._harness.Store(binding).WriteAsync(
            key,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "the-one" },
            DateTimeOffset.UtcNow.AddMinutes(10));

        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, claimants).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == claimants)
            {
                allArrived.SetResult();
            }

            await release.Task;

            // A store of its own per claimant, which a second delivery arriving on another replica is.
            return await this._harness.Store(binding).ClaimAsync(key);
        })).ToList();

        await allArrived.Task.WaitAsync(TestTimeout);
        release.SetResult();
        var outcomes = await Task.WhenAll(attempts).WaitAsync(TestTimeout);

        Assert.Single(outcomes, outcome => outcome.Claimed);
        Assert.Equal("the-one", outcomes.Single(outcome => outcome.Claimed).Values["value"]);
        Assert.All(
            outcomes.Where(outcome => !outcome.Claimed),
            outcome => Assert.Equal(ProviderClaimRefusal.AlreadyConsumed, outcome.Refusal));
    }

    // The sweep is what keeps an add-in that starts flows and never finishes them from filling the table. It
    // takes the family's own expired and consumed rows and nothing else.
    [Fact]
    public async Task ExpiredAndConsumedEntriesAreSweptAndAnUnexpiredOneIsLeftAlone()
    {
        var binding = await this._harness.SeedConnectionAsync("Swept");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = this._harness.Store(binding, timeProvider: clock);

        var expired = NewKey();
        var live = NewKey();
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["value"] = "x" };

        await store.WriteAsync(expired, values, clock.GetUtcNow().AddMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(6));
        await store.WriteAsync(live, values, clock.GetUtcNow().AddMinutes(30));

        await using var db = this._harness.CreateContext();
        var remaining = await db.ProviderKeyedEntries
            .AsNoTracking()
            .Where(entry => entry.AddInKey == binding.AddInKey)
            .Select(entry => entry.EntryKey)
            .ToListAsync();

        Assert.DoesNotContain(expired, remaining);
        Assert.Contains(live, remaining);
    }

    // A family stores named values and never an encoded form, so what lands in the column is what an operator
    // would find in a backup: nothing readable.
    [Fact]
    public async Task TheStoredValueIsNotReadableFromTheColumnWithoutTheHostsProtection()
    {
        var binding = await this._harness.SeedConnectionAsync("Protected entry");
        var key = NewKey();

        await this._harness.Store(binding).WriteAsync(
            key,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["codeVerifier"] = "a-very-secret-verifier" },
            DateTimeOffset.UtcNow.AddMinutes(10));

        await using var db = this._harness.CreateContext();
        var column = await db.ProviderKeyedEntries
            .AsNoTracking()
            .Where(entry => entry.AddInKey == binding.AddInKey && entry.EntryKey == key)
            .Select(entry => entry.ProtectedValue)
            .SingleAsync();

        Assert.DoesNotContain("a-very-secret-verifier", column, StringComparison.Ordinal);
        Assert.DoesNotContain("codeVerifier", column, StringComparison.Ordinal);
    }

    // Two callers writing one new key at once. Read and then inserted, both find no row and both insert, and the
    // second is refused by the unique index instead of replacing what the first wrote — which is a flow restarted
    // under the same state value failing for a reason the family can do nothing about.
    [Fact]
    public async Task TwoConcurrentWritesOfOneNewKeyBothSucceedAndTheEntryIsClaimableOnce()
    {
        var binding = await this._harness.SeedConnectionAsync("Concurrent writes");
        var key = NewKey();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

        await Task.WhenAll(
                Enumerable.Range(0, 2).Select(index => Task.Run(() =>
                    this._harness.Store(binding).WriteAsync(
                        key,
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["writer"] = index.ToString() },
                        expiry))))
            .WaitAsync(TestTimeout);

        await using (var db = this._harness.CreateContext())
        {
            var rows = await db.ProviderKeyedEntries
                .AsNoTracking()
                .CountAsync(entry => entry.AddInKey == binding.AddInKey && entry.EntryKey == key);

            Assert.Equal(1, rows);
        }

        var store = this._harness.Store(binding);
        Assert.True((await store.ClaimAsync(key)).Claimed);
        Assert.False((await store.ClaimAsync(key)).Claimed);
    }

    /// <summary>A key nothing else in the shared database is using.</summary>
    private static string NewKey()
    {
        return Guid.NewGuid().ToString("N");
    }
}
