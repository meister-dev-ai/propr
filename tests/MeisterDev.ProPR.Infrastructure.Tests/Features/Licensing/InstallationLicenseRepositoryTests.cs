// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.RemoveLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

public sealed class InstallationLicenseRepositoryTests
{
    /// <summary>The envelope the secret-protection codec writes. A stored license has to carry it.</summary>
    private const string ProtectedPrefix = "mpr-protected:v1:";

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoLicenseOnFile_ReadsAsNothingStored()
    {
        await using var db = CreateContext();
        var sut = CreateRepository(db);

        Assert.Null(await sut.GetAsync());
    }

    // The document is what the installation's entitlement rests on, so it must not sit in the database in a
    // form anything that can read the table can also read.
    [Fact]
    public async Task AnActivatedLicense_IsStoredProtectedAndReadBackIntact()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var actor = Guid.NewGuid();

        await using var db = CreateContext();
        var sut = CreateRepository(db);

        await sut.SetAsync(compactLicense, actor);

        var storedRow = await db.InstallationLicenses.AsNoTracking().SingleAsync();
        Assert.StartsWith(ProtectedPrefix, storedRow.ProtectedToken, StringComparison.Ordinal);
        Assert.DoesNotContain(compactLicense, storedRow.ProtectedToken, StringComparison.Ordinal);

        var stored = await sut.GetAsync();
        Assert.NotNull(stored);
        Assert.True(stored.IsReadable);
        Assert.Equal(compactLicense, stored.CompactLicense);
        Assert.Equal(Now, stored.ActivatedAt);
        Assert.Equal(actor, stored.ActivatedByUserId);
    }

    // A value the installation's keys cannot open is reported as unreadable rather than raised, because the
    // caller has a state for it that says something different from a license that fails to verify.
    [Fact]
    public async Task AnAlteredStoredValue_ReadsAsUnreadable()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));

        await using var db = CreateContext();
        var sut = CreateRepository(db);
        await sut.SetAsync(compactLicense, null);

        var storedRow = await db.InstallationLicenses.SingleAsync();
        storedRow.ProtectedToken = AlterProtectedPayload(storedRow.ProtectedToken);
        await db.SaveChangesAsync();

        var stored = await sut.GetAsync();
        Assert.NotNull(stored);
        Assert.False(stored.IsReadable);
        Assert.Null(stored.CompactLicense);
        Assert.Equal(Now, stored.ActivatedAt);
    }

    // An installation holds one license, so activating again replaces what was there instead of adding a row.
    [Fact]
    public async Task ASecondActivation_ReplacesTheFirst()
    {
        using var chain = LicenseTestChain.Create();
        var first = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-10), Now.AddDays(10)));
        var second = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(2)));

        await using var db = CreateContext();
        var timeProvider = new FakeTimeProvider(Now);
        var sut = new InstallationLicenseRepository(db, CreateCodec(), timeProvider);

        Assert.Null((await sut.ReplaceAsync(first, null)).Previous);
        timeProvider.Advance(TimeSpan.FromHours(3));
        var displaced = await sut.ReplaceAsync(second, null);

        Assert.Equal(1, await db.InstallationLicenses.CountAsync());
        Assert.NotNull(displaced.Previous);
        Assert.Equal(first, displaced.Previous.CompactLicense);
        var stored = await sut.GetAsync();
        Assert.NotNull(stored);
        Assert.Equal(second, stored.CompactLicense);
        Assert.Equal(Now.AddHours(3), stored.ActivatedAt);
    }

    [Fact]
    public async Task RemovingTheLicense_LeavesNothingStored()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));

        await using var db = CreateContext();
        var sut = CreateRepository(db);
        await sut.SetAsync(compactLicense, null);

        var removed = await sut.RemoveAndGetAsync();

        Assert.NotNull(removed.Previous);
        Assert.Equal(compactLicense, removed.Previous.CompactLicense);
        Assert.Null(await sut.GetAsync());
        Assert.Equal(0, await db.InstallationLicenses.CountAsync());
    }

    [Fact]
    public async Task RemovingWithNothingStored_IsNotAnError()
    {
        await using var db = CreateContext();
        var sut = CreateRepository(db);

        Assert.Null((await sut.RemoveAndGetAsync()).Previous);

        Assert.Null(await sut.GetAsync());
    }

    /// <summary>
    ///     Changes one character inside the protected envelope, which is what an edit to the stored value
    ///     amounts to. The envelope is authenticated, so any change to it stops it opening.
    /// </summary>
    internal static string AlterProtectedPayload(string protectedValue)
    {
        var payloadStart = protectedValue.IndexOf(':', StringComparison.Ordinal);
        var target = (payloadStart + protectedValue.Length) / 2;

        return string.Concat(
            protectedValue.AsSpan(0, target),
            protectedValue[target] == 'A' ? "B" : "A",
            protectedValue.AsSpan(target + 1));
    }

    internal static ISecretProtectionCodec CreateCodec() => new SecretProtectionCodec(new EphemeralDataProtectionProvider());

    private static InstallationLicenseRepository CreateRepository(MeisterProPRDbContext db)
        => new(db, CreateCodec(), new FakeTimeProvider(Now));

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_InstallationLicense_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

[Collection("PostgresIntegration")]
public sealed class InstallationLicenseRepositoryPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    // The mutation lock is what serializes the two writes: the replica that goes second reads the row the
    // first one wrote and updates it. Two replicas activating together must both succeed and leave one row,
    // because a second insert would otherwise fail on the primary key.
    [Fact]
    public async Task TwoReplicasActivatingTogether_LeaveOneLicenseOnFile()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var codec = InstallationLicenseRepositoryTests.CreateCodec();

        await using var first = this.CreatePostgresContext();
        await using var second = this.CreatePostgresContext();

        await Task.WhenAll(
            new InstallationLicenseRepository(first, codec, new FakeTimeProvider(Now)).SetAsync(compactLicense, null),
            new InstallationLicenseRepository(second, codec, new FakeTimeProvider(Now)).SetAsync(compactLicense, null));

        await using var verification = this.CreatePostgresContext();
        var storedRow = await verification.InstallationLicenses.AsNoTracking().SingleAsync();
        Assert.Equal(1, storedRow.Id);
        Assert.Equal(Now, storedRow.ActivatedAt);

        var stored = await new InstallationLicenseRepository(verification, codec, new FakeTimeProvider(Now)).GetAsync();
        Assert.NotNull(stored);
        Assert.Equal(compactLicense, stored.CompactLicense);
    }

    // The history is ordered by the instant a record carries, so that instant has to come from one clock. Each
    // replica has its own, and two of them disagreeing by minutes would order a removal after the activation
    // that replaced it. The store reads the database clock inside the transaction its lock is held in, and the
    // handler records that value: here the replica's clock says 2026 while the database says today, and the
    // recorded instant is the database's.
    [Fact]
    public async Task AMutationRecord_CarriesTheInstantTheStoreObservedRatherThanTheReplicaClock()
    {
        using var chain = LicenseTestChain.Create();
        using var anchor = chain.CreateAnchor();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var codec = InstallationLicenseRepositoryTests.CreateCodec();

        var before = DateTimeOffset.UtcNow;

        await using var activating = this.CreatePostgresContext();
        await CreateActivationHandler(activating, codec, anchor)
            .HandleAsync(new ActivateLicenseCommand(compactLicense, null));

        await using var removing = this.CreatePostgresContext();
        await CreateRemovalHandler(removing, codec, anchor)
            .HandleAsync(new RemoveLicenseCommand(null));

        var after = DateTimeOffset.UtcNow;

        await using var reading = this.CreatePostgresContext();
        var recorded = await new LicenseActivationEventRepository(reading).ListRecentAsync(10);

        Assert.Equal(2, recorded.Count);
        Assert.Equal(LicenseActivationAction.Removed, recorded[0].Action);
        Assert.Equal(LicenseActivationAction.Activated, recorded[1].Action);

        foreach (var activationEvent in recorded)
        {
            Assert.InRange(activationEvent.OccurredAt, before.AddMinutes(-1), after.AddMinutes(1));
        }

        // The removal is the later mutation, so it has to sort after the activation on the recorded instants
        // alone rather than on the order the two records happened to be appended in.
        Assert.True(recorded[0].OccurredAt >= recorded[1].OccurredAt);
    }

    // The stored value has to survive a round trip through the column's own type, which the in-memory provider
    // does not exercise.
    [Fact]
    public async Task AnActivatedLicense_SurvivesANewConnection()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var codec = InstallationLicenseRepositoryTests.CreateCodec();

        await using var writing = this.CreatePostgresContext();
        await new InstallationLicenseRepository(writing, codec, new FakeTimeProvider(Now)).SetAsync(compactLicense, null);

        await using var reading = this.CreatePostgresContext();
        var stored = await new InstallationLicenseRepository(reading, codec, new FakeTimeProvider(Now)).GetAsync();

        Assert.NotNull(stored);
        Assert.Equal(compactLicense, stored.CompactLicense);
    }

    // Removal on PostgreSQL goes through the bulk delete, which the in-memory provider does not implement, so
    // the production statement is exercised here. Repeating it stands in for a second replica having already
    // removed the row.
    [Fact]
    public async Task RemovingTheLicense_LeavesNothingStoredAndRepeatsWithoutFailing()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var codec = InstallationLicenseRepositoryTests.CreateCodec();

        await using var db = this.CreatePostgresContext();
        var sut = new InstallationLicenseRepository(db, codec, new FakeTimeProvider(Now));
        await sut.SetAsync(compactLicense, null);

        await sut.RemoveAsync();
        await sut.RemoveAsync();

        Assert.Null(await sut.GetAsync());
        Assert.Equal(0, await db.InstallationLicenses.CountAsync());
    }

    [Fact]
    public async Task ConcurrentActivations_RecordOneActivationAndOneReplacement()
    {
        using var chain = LicenseTestChain.Create();
        using var anchor = chain.CreateAnchor();
        var firstLicenseId = "1d9d6d10-b14d-4c52-b651-4fb7c1d6fd61";
        var secondLicenseId = "65c154b2-02d8-4d55-ba3b-c1ee194e187b";
        var firstDocument = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)) with { LicenseId = firstLicenseId });
        var secondDocument = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)) with { LicenseId = secondLicenseId });
        var codec = InstallationLicenseRepositoryTests.CreateCodec();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var first = this.CreatePostgresContext();
        await using var second = this.CreatePostgresContext();
        var firstHandler = CreateActivationHandler(first, codec, anchor);
        var secondHandler = CreateActivationHandler(second, codec, anchor);

        var firstActivation = Task.Run(async () =>
        {
            await release.Task;
            return await firstHandler.HandleAsync(new ActivateLicenseCommand(firstDocument, Guid.NewGuid()));
        });
        var secondActivation = Task.Run(async () =>
        {
            await release.Task;
            return await secondHandler.HandleAsync(new ActivateLicenseCommand(secondDocument, Guid.NewGuid()));
        });
        release.SetResult();
        var results = await Task.WhenAll(firstActivation, secondActivation);

        Assert.All(results, result => Assert.True(result.IsActivated));

        await using var verification = this.CreatePostgresContext();
        var events = await verification.LicenseActivationEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Single(events, entry => entry.Action == LicenseActivationAction.Activated);
        Assert.Single(events, entry => entry.Action == LicenseActivationAction.Replaced);
        Assert.Equal([firstLicenseId, secondLicenseId], events.Select(entry => entry.LicenseId).OrderBy(id => id));

        // Which of the two won the lock is not decided here, but the correlation is: the document left on
        // file is the one whose activation replaced the other. Asserting the pair rather than the set is what
        // fails if the two records were to carry each other's license.
        var replacedEvent = events.Single(entry => entry.Action == LicenseActivationAction.Replaced);
        var activatedEvent = events.Single(entry => entry.Action == LicenseActivationAction.Activated);
        var stored = await new InstallationLicenseRepository(verification, codec, new FakeTimeProvider(Now)).GetAsync();

        Assert.NotNull(stored);
        Assert.Equal(
            string.Equals(stored.CompactLicense, firstDocument, StringComparison.Ordinal)
                ? firstLicenseId
                : secondLicenseId,
            replacedEvent.LicenseId);
        Assert.NotEqual(replacedEvent.LicenseId, activatedEvent.LicenseId);
    }

    [Fact]
    public async Task ConcurrentActivationAndRemoval_LeaveHistoryForTheDocumentActuallyRemoved()
    {
        using var chain = LicenseTestChain.Create();
        using var anchor = chain.CreateAnchor();
        var oldLicenseId = "3e90e239-0f3e-4d21-b7cb-2dff0d801e8f";
        var newLicenseId = "d5ae57be-4382-4733-95e7-076a9e53fbd9";
        var oldDocument = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)) with { LicenseId = oldLicenseId });
        var newDocument = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)) with { LicenseId = newLicenseId });
        var codec = InstallationLicenseRepositoryTests.CreateCodec();
        await using (var seed = this.CreatePostgresContext())
        {
            await new InstallationLicenseRepository(seed, codec, new FakeTimeProvider(Now)).SetAsync(oldDocument, null);
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var activationContext = this.CreatePostgresContext();
        await using var removalContext = this.CreatePostgresContext();
        var activation = CreateActivationHandler(activationContext, codec, anchor);
        var removal = CreateRemovalHandler(removalContext, codec, anchor);

        var activationTask = Task.Run(async () =>
        {
            await release.Task;
            await activation.HandleAsync(new ActivateLicenseCommand(newDocument, Guid.NewGuid()));
        });
        var removalTask = Task.Run(async () =>
        {
            await release.Task;
            await removal.HandleAsync(new RemoveLicenseCommand(Guid.NewGuid()));
        });
        release.SetResult();
        await Task.WhenAll(activationTask, removalTask);

        await using var verification = this.CreatePostgresContext();
        var removalEvent = await verification.LicenseActivationEvents.AsNoTracking()
            .SingleAsync(entry => entry.Action == LicenseActivationAction.Removed);
        var stored = await new InstallationLicenseRepository(verification, codec, new FakeTimeProvider(Now)).GetAsync();

        if (stored is null)
        {
            Assert.Equal(newLicenseId, removalEvent.LicenseId);
        }
        else
        {
            Assert.Equal(newDocument, stored.CompactLicense);
            Assert.Equal(oldLicenseId, removalEvent.LicenseId);
        }
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreatePostgresContext();
        await db.LicenseActivationEvents.ExecuteDeleteAsync();
        await db.InstallationLicenses.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static ActivateLicenseHandler CreateActivationHandler(
        MeisterProPRDbContext dbContext,
        ISecretProtectionCodec codec,
        LicenseTrustAnchor anchor)
    {
        var stateProvider = Substitute.For<ILicenseStateProvider>();
        var capabilityService = Substitute.For<ILicensingCapabilityService>();
        capabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(new LicensingSummaryDto(InstallationEdition.Commercial, Now, []));
        var clock = Substitute.For<ILicensingClock>();
        clock.GetUtcNowAsync(Arg.Any<CancellationToken>()).Returns(Now);

        return new ActivateLicenseHandler(
            new LicenseVerifier(anchor),
            new InstallationLicenseRepository(dbContext, codec, new FakeTimeProvider(Now)),
            new LicenseActivationEventRepository(dbContext),
            stateProvider,
            capabilityService,
            clock,
            NullLogger<ActivateLicenseHandler>.Instance);
    }

    private static RemoveLicenseHandler CreateRemovalHandler(
        MeisterProPRDbContext dbContext,
        ISecretProtectionCodec codec,
        LicenseTrustAnchor anchor)
    {
        return new RemoveLicenseHandler(
            new LicenseVerifier(anchor),
            new InstallationLicenseRepository(dbContext, codec, new FakeTimeProvider(Now)),
            new LicenseActivationEventRepository(dbContext),
            Substitute.For<ILicenseStateProvider>(),
            new FakeTimeProvider(Now),
            NullLogger<RemoveLicenseHandler>.Instance);
    }
}
