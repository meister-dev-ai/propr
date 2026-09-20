// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.ExampleAddIn;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     The credential session against a real PostgreSQL instance.
/// </summary>
/// <remarks>
///     These have to run against the database. What the session is for is that the row lock, the read, the
///     vendor exchange and the write share one transaction, and a double that answered from memory would prove
///     nothing about any of it: the property being tested is that the database serialises callers that a
///     single-threaded test never puts in contention.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class ProviderCredentialSessionTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    /// <summary>
    ///     Bounds every wait these tests make, so a lock that is taken and not given back fails the run instead
    ///     of hanging it.
    /// </summary>
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

    // The defect this rules out: ten callers on one connection each starting their own renewal. With a
    // credential the vendor rotates, the second exchange invalidates what the first stored and the connection is
    // lost. One exchange happens because the lock, the re-read and the write share a transaction, so the nine
    // that waited find the renewed credential and take it.
    [Fact]
    public async Task TenConcurrentRenewalsOfOneExpiringCredential_ExchangeOnceAndAllTenGetAUsableToken()
    {
        const int callers = 10;

        var binding = await this._harness.SeedConnectionAsync("Ten callers");
        var now = DateTimeOffset.UtcNow;
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ExampleCredentialRenewal.AccessTokenField] = "the-old-token",
                [ExampleCredentialRenewal.RefreshTokenField] = "the-old-renewal",
            },

            // Inside the renewal margin, so every caller finds it expiring.
            now.AddSeconds(30));

        var exchanges = 0;
        var context = this.ContextFor(binding);

        // Every caller reports itself ready and waits for one signal, so all ten ask together. Started one after
        // another, the first would finish before the last began and the test would pass without contention.
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == callers)
            {
                allArrived.SetResult();
            }

            await release.Task;

            return await ExampleCredentialRenewal.CurrentAsync(
                context,
                async (renewalToken, ct) =>
                {
                    Interlocked.Increment(ref exchanges);

                    // A vendor exchange takes time, which is the whole reason the lock is held across it.
                    await Task.Delay(50, ct);
                    return new ExampleRenewedCredential(
                        "the-new-token",
                        renewalToken + "-rotated",
                        now.AddHours(1));
                },
                now);
        })).ToList();

        await allArrived.Task.WaitAsync(TestTimeout);
        release.SetResult();
        var tokens = await Task.WhenAll(attempts).WaitAsync(TestTimeout);

        Assert.Equal(1, exchanges);
        Assert.All(tokens, token => Assert.Equal("the-new-token", token));

        // The renewal token the winner stored is the rotated one, so the losers did not write the value they
        // started from back over it.
        var stored = await context.Credentials.ReadAsync();
        Assert.Equal("the-old-renewal-rotated", stored.Fields[ExampleCredentialRenewal.RefreshTokenField]);
    }

    // The lock, the read and the write are one transaction, shown by a second caller not seeing the write until
    // the first commits. Without the shared transaction the second would read the new credential early and the
    // single-flight property would rest on timing.
    [Fact]
    public async Task AConcurrentCallerSeesNothingWrittenInASessionUntilItIsCompleted()
    {
        var binding = await this._harness.SeedConnectionAsync("Held open");
        var credentials = this._harness.Credentials(binding);

        await using var session = await credentials.OpenAsync();
        await session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "written-inside" },
            DateTimeOffset.UtcNow.AddHours(1));

        // Read from a context of its own, which a parallel pass of a review is.
        var readOutside = await this._harness.Credentials(binding).ReadAsync();
        Assert.False(readOutside.IsPresent);

        await session.CompleteAsync();

        var readAfter = await this._harness.Credentials(binding).ReadAsync();
        Assert.Equal("written-inside", readAfter.Fields["accessToken"]);
    }

    // A session abandoned without being completed leaves the credential it started from, rather than a half
    // written one: a failed vendor exchange must not cost the connection the credential it already had.
    [Fact]
    public async Task ASessionDisposedWithoutBeingCompletedLeavesTheCredentialItStartedFrom()
    {
        var binding = await this._harness.SeedConnectionAsync("Rolled back");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "the-original" },
            DateTimeOffset.UtcNow.AddHours(1));

        var credentials = this._harness.Credentials(binding);
        await using (var session = await credentials.OpenAsync())
        {
            await session.WriteAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "abandoned" },
                DateTimeOffset.UtcNow.AddHours(1));
        }

        var stored = await credentials.ReadAsync();
        Assert.Equal("the-original", stored.Fields["accessToken"]);
    }

    // A caller that waits longer than the host allows is told the wait failed, and the failure is one the retry
    // stage classifies as transient: a renewal held up behind another caller's vendor exchange is worth
    // repeating, and reporting it as a failure would abandon a review over a wait.
    [Fact]
    public async Task ACallerThatExceedsTheLockWaitFailsInAWayTheRetryStageTreatsAsTransient()
    {
        var binding = await this._harness.SeedConnectionAsync("Contended");

        await using var holder = await this._harness.Credentials(binding).OpenAsync();

        // The host's own wait is stated in tens of seconds, so this caller is given a shorter one rather than
        // the test waiting it out. What it exercises is the same path, with the same refusal at the end of it.
        var impatient = this._harness.Credentials(binding, lockWait: TimeSpan.FromMilliseconds(250));

        var failure = await Assert.ThrowsAsync<ProviderHostTimeoutException>(() => impatient.OpenAsync());

        Assert.Contains("Contended", failure.Message, StringComparison.Ordinal);
        Assert.True(DriverFailureMapper.ClassifyRuntimeFailure(failure).IsTransient);
    }

    // PostgreSQL reads a lock_timeout of zero as no timeout, so a wait that rounds down to nothing would make a
    // caller asking not to wait wait for ever.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ACallerThatAsksNotToWaitIsRefusedRatherThanHeld(int milliseconds)
    {
        var binding = await this._harness.SeedConnectionAsync("Asks not to wait");

        await using var holder = await this._harness.Credentials(binding).OpenAsync();

        var impatient = this._harness.Credentials(binding, lockWait: TimeSpan.FromMilliseconds(milliseconds));

        await Assert.ThrowsAsync<ProviderHostTimeoutException>(() => impatient.OpenAsync().WaitAsync(TestTimeout));
    }

    // A review builds its client once and calls it for as long as the review runs, so by the tenth pass the work
    // that built the client is over. Nothing the credential handle needs is resolved from that work, which
    // lets the client keep it.
    [Fact]
    public async Task ACredentialHandleBuiltInsideAScopeStillAnswersAfterThatScopeIsGone()
    {
        var binding = await this._harness.SeedConnectionAsync("Outlives its scope");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "still-here" },
            DateTimeOffset.UtcNow.AddHours(1));

        IProviderCredentialSessions credentials;
        var services = new ServiceCollection();
        services.AddSingleton(this._harness.Contexts);
        await using (var provider = services.BuildServiceProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            credentials = this._harness.Credentials(binding);

            // Read once inside, so the handle is in the state it would be in when a review's first pass runs.
            Assert.True((await credentials.ReadAsync()).IsPresent);
        }

        var afterwards = await credentials.ReadAsync();

        Assert.Equal("still-here", afterwards.Fields["accessToken"]);
    }

    // Many threads at once is the ordinary case: a review runs its passes in parallel against one connection.
    // Each call opening a context of its own is what keeps Entity Framework from being driven concurrently
    // through one.
    [Fact]
    public async Task ConcurrentReadsFromManyThreadsAllReturnTheCredentialWithNoConcurrencyFailure()
    {
        const int readers = 16;

        var binding = await this._harness.SeedConnectionAsync("Read in parallel");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "shared" },
            DateTimeOffset.UtcNow.AddHours(1));

        var credentials = this._harness.Credentials(binding);

        var reads = await Task.WhenAll(Enumerable.Range(0, readers).Select(_ => Task.Run(() => credentials.ReadAsync())))
            .WaitAsync(TestTimeout);

        Assert.All(reads, read => Assert.Equal("shared", read.Fields["accessToken"]));
    }

    // The credential is the one thing an add-in cannot reach any other way, so the licence check on use sits
    // here and on the read beside it. The other checks fire when a connection is configured and never when one
    // is used, so without this an installation whose entitlement lapsed keeps running reviews.
    [Fact]
    public async Task AnInstallationWhoseEntitlementLapsedCannotReadOrTakeTheCredential()
    {
        var binding = await this._harness.SeedConnectionAsync(
            "Unlicensed",
            requiredCapabilityKey: "example-connections");

        this._harness.Capabilities
            .IsAvailableAsync("example-connections", Arg.Any<CancellationToken>())
            .Returns(false);

        var credentials = this._harness.Credentials(binding);

        var onRead = await Assert.ThrowsAsync<ProviderCapabilityUnavailableException>(() => credentials.ReadAsync());
        var onOpen = await Assert.ThrowsAsync<ProviderCapabilityUnavailableException>(() => credentials.OpenAsync());

        Assert.Contains("example-connections", onRead.Message, StringComparison.Ordinal);
        Assert.Contains("example-connections", onOpen.Message, StringComparison.Ordinal);
    }

    // A credential with no stated expiry reads as never usable everywhere it is read, so accepting one produces
    // a connection that verifies and then renews before every call, serialising a whole review through one row
    // lock. The refusal names the field while the value is still in the family's hands.
    [Fact]
    public async Task StoringACredentialWithNoExpiryIsRefusedAndTheRefusalNamesTheField()
    {
        var binding = await this._harness.SeedConnectionAsync("No expiry");
        var credentials = this._harness.Credentials(binding);

        await using var session = await credentials.OpenAsync();

        var refusal = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-token" },
            default));

        Assert.Equal("expiresAt", refusal.ParamName);
        Assert.Contains("expiresAt", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoringACredentialWhoseExpiryHasPassedIsRefusedWithItsOwnMessage()
    {
        var binding = await this._harness.SeedConnectionAsync("Past expiry");
        var credentials = this._harness.Credentials(binding);

        await using var session = await credentials.OpenAsync();

        var refusal = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-token" },
            DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Contains("already passed", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoringACredentialWithAFutureExpirySucceedsAndReadsBackWithIt()
    {
        var binding = await this._harness.SeedConnectionAsync("Good expiry");
        var credentials = this._harness.Credentials(binding);
        var expiry = DateTimeOffset.UtcNow.AddHours(2);

        await using (var session = await credentials.OpenAsync())
        {
            await session.WriteAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-token" },
                expiry);
            await session.CompleteAsync();
        }

        var stored = await credentials.ReadAsync();
        Assert.Equal("a-token", stored.Fields["accessToken"]);
        Assert.NotNull(stored.ExpiresAt);
        Assert.Equal(expiry, stored.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    // The column is what an operator would find in a backup or a replica, and what is in it is protected: a
    // family deals in named values and never in the stored form.
    [Fact]
    public async Task TheStoredCredentialIsNotReadableFromTheColumnWithoutTheHostsProtection()
    {
        var binding = await this._harness.SeedConnectionAsync("Protected at rest");
        var credentials = this._harness.Credentials(binding);

        await using (var session = await credentials.OpenAsync())
        {
            await session.WriteAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-very-secret-token" },
                DateTimeOffset.UtcNow.AddHours(1));
            await session.CompleteAsync();
        }

        await using var db = this._harness.CreateContext();
        var column = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == binding.ConnectionProfileId)
            .Select(profile => profile.ProtectedSecret)
            .SingleAsync();

        Assert.NotNull(column);
        Assert.DoesNotContain("a-very-secret-token", column, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", column, StringComparison.Ordinal);
    }

    // A family holds its handle for the life of the client the host built for it, which outlives the work that
    // built it. A connection repointed to another family in that time keeps its identifier, so a handle that
    // checked the identifier alone would read the arriving family's credential.
    [Fact]
    public async Task AHandleHeldAcrossARepointReadsNothingAndSaysWhichFamilyTheConnectionNowHas()
    {
        var binding = await this._harness.SeedConnectionAsync("Repointed while held");
        var credentials = this._harness.Credentials(binding);

        await using (var session = await credentials.OpenAsync())
        {
            await session.WriteAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "the-old-familys" },
                DateTimeOffset.UtcNow.AddHours(1));
            await session.CompleteAsync();
        }

        await using (var db = this._harness.CreateContext())
        {
            await db.AiConnectionProfiles
                .Where(profile => profile.Id == binding.ConnectionProfileId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    profile => profile.ProviderKind,
                    "OpenAi"));
        }

        var onRead = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => credentials.ReadAsync());
        var onOpen = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => credentials.OpenAsync());

        Assert.Contains("OpenAi", onRead.Message, StringComparison.Ordinal);
        Assert.Contains("OpenAi", onOpen.Message, StringComparison.Ordinal);
    }

    // A family has more than one spelling of its identity, and a row can be rewritten from one to another
    // without changing which family it belongs to. Comparing the stored text would make that rewrite look like a
    // repoint and refuse the credential of every connection it touched — at review time, on a family whose
    // credential an operator may not be able to obtain again.
    [Fact]
    public async Task ARowRespelledOntoTheFamilysOwnIdentitiesIsNotARepoint()
    {
        var binding = await this._harness.SeedConnectionAsync(
            "Respelled in place",
            supersededIdentities: ["LegacyCompatible", "OpenAiCompatible"]);
        var credentials = this._harness.Credentials(binding);

        await using (var session = await credentials.OpenAsync())
        {
            await session.WriteAsync(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = "still-this-familys" },
                DateTimeOffset.UtcNow.AddHours(1));
            await session.CompleteAsync();
        }

        foreach (var spelling in new[] { binding.AddInKey, "LegacyCompatible", "OpenAiCompatible" })
        {
            await using (var db = this._harness.CreateContext())
            {
                await db.AiConnectionProfiles
                    .Where(profile => profile.Id == binding.ConnectionProfileId)
                    .ExecuteUpdateAsync(update => update.SetProperty(profile => profile.ProviderKind, spelling));
            }

            var credential = await credentials.ReadAsync();

            Assert.Equal("still-this-familys", credential.Fields["apiKey"]);

            await using var held = await credentials.OpenAsync();
            await held.CompleteAsync();
        }
    }

    // The entitlement is read again with the row held. The wait for that row is as long as whatever the caller
    // ahead is doing with the vendor, and an answer given before that wait says nothing about the moment the
    // credential is handed over.
    [Fact]
    public async Task AnEntitlementThatLapsesWhileTheRowIsBeingWaitedForStopsTheSessionBeingHandedOut()
    {
        var binding = await this._harness.SeedConnectionAsync(
            "Lapsed during the wait",
            requiredCapabilityKey: "example-connections");

        // Yes to the caller arriving, no by the time the row is theirs. Checked once, the session would be handed
        // out on the first answer.
        this._harness.Capabilities
            .IsAvailableAsync("example-connections", Arg.Any<CancellationToken>())
            .Returns(true, false);

        var credentials = this._harness.Credentials(binding);

        var refusal = await Assert.ThrowsAsync<ProviderCapabilityUnavailableException>(() => credentials.OpenAsync());

        Assert.Contains("example-connections", refusal.Message, StringComparison.Ordinal);
    }

    // A blank value reads back as absent everywhere a credential is read, so a map of blanks is a connection
    // that verifies, appears configured and presents nothing.
    [Fact]
    public async Task StoringACredentialWhoseFieldsAreAllBlankIsRefused()
    {
        var binding = await this._harness.SeedConnectionAsync("All blank");
        var credentials = this._harness.Credentials(binding);

        await using var session = await credentials.OpenAsync();

        var refusal = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "   " },
            DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal("fields", refusal.ParamName);
    }

    // What an operator disconnecting an account runs. The connection keeps its address and its models and holds
    // no credential, which is the state it was in before the credential flow was run against it.
    [Fact]
    public async Task ClearingTheCredentialRemovesItAndWhatWasRecordedAboutItWhileKeepingTheConnection()
    {
        var binding = await this._harness.SeedConnectionAsync("Disconnected");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-grant" },
            DateTimeOffset.UtcNow.AddHours(1));

        await using (var db = this._harness.CreateContext())
        {
            await db.AiConnectionProfiles
                .Where(profile => profile.Id == binding.ConnectionProfileId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(profile => profile.CredentialHealth, AiCredentialHealth.Healthy.ToString())
                    .SetProperty(profile => profile.CredentialHealthCause, "The provider accepted it.")
                    .SetProperty(profile => profile.CredentialHealthChangedAt, DateTimeOffset.UtcNow)
                    .SetProperty(profile => profile.CredentialOwnerAdminId, Guid.NewGuid())
                    .SetProperty(profile => profile.CredentialOwnerDisplayName, "An administrator")
                    .SetProperty(profile => profile.CredentialAuthorizedAt, DateTimeOffset.UtcNow));
        }

        var credentials = this._harness.Credentials(binding);

        await using (var session = await credentials.OpenAsync())
        {
            await session.ClearAsync();
            await session.CompleteAsync();
        }

        Assert.False((await credentials.ReadAsync()).IsPresent);

        await using var reading = this._harness.CreateContext();
        var row = await reading.AiConnectionProfiles
            .SingleAsync(profile => profile.Id == binding.ConnectionProfileId);

        Assert.Null(row.ProtectedSecret);
        Assert.Null(row.CredentialHealth);
        Assert.Null(row.CredentialHealthCause);
        Assert.Null(row.CredentialHealthChangedAt);
        Assert.Null(row.CredentialOwnerAdminId);
        Assert.Null(row.CredentialOwnerDisplayName);
        Assert.Null(row.CredentialAuthorizedAt);
        Assert.Equal("Disconnected", row.DisplayName);
    }

    // The clear and the commit are one transaction, so a session abandoned between them leaves the credential
    // where it was rather than half removed.
    [Fact]
    public async Task AClearOnASessionThatIsNeverCompletedLeavesTheCredentialInPlace()
    {
        var binding = await this._harness.SeedConnectionAsync("Abandoned clear");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessToken"] = "a-grant" },
            DateTimeOffset.UtcNow.AddHours(1));

        var credentials = this._harness.Credentials(binding);

        await using (var session = await credentials.OpenAsync())
        {
            await session.ClearAsync();
        }

        Assert.Equal("a-grant", (await credentials.ReadAsync()).Fields["accessToken"]);
    }

    private IProviderConnectionContext ContextFor(ProviderAddInBinding binding)
    {
        return new ProviderConnectionContext(
            Substitute.For<IProviderHttpClientFactory>(),
            this._harness.Credentials(binding),
            this._harness.Leases(binding),
            this._harness.Store(binding),
            Substitute.For<IProviderHealthSignal>());
    }
}
