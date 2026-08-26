// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

public sealed class LicensingIdentityRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFreshInstallation_GetsAnIdentifierOnTheFirstRead()
    {
        await using var db = CreateContext();
        var sut = new LicensingIdentityRepository(db, new FakeTimeProvider(Now));

        var identifier = await sut.GetOrCreateAsync();

        Assert.NotEqual(Guid.Empty, identifier);
        var storedRow = await db.LicensingIdentity.AsNoTracking().SingleAsync();
        Assert.Equal(1, storedRow.Id);
        Assert.Equal(identifier, storedRow.Identifier);
        Assert.Equal(Now, storedRow.CreatedAt);
    }

    // The identifier is what links one installation's reports and support conversations over time, so it has to
    // survive every later read.
    [Fact]
    public async Task TheIdentifier_IsCreatedOnceAndKept()
    {
        await using var db = CreateContext();
        var sut = new LicensingIdentityRepository(db, new FakeTimeProvider(Now));

        var first = await sut.GetOrCreateAsync();
        var second = await sut.GetOrCreateAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, await db.LicensingIdentity.CountAsync());
    }

    // Nothing about the environment goes into the identifier, so two installations on the same host under the
    // same clock still report themselves as two.
    [Fact]
    public async Task TwoFreshDatabases_ProduceDifferentIdentifiers()
    {
        await using var first = CreateContext();
        await using var second = CreateContext();

        var firstIdentifier = await new LicensingIdentityRepository(first, new FakeTimeProvider(Now)).GetOrCreateAsync();
        var secondIdentifier = await new LicensingIdentityRepository(second, new FakeTimeProvider(Now)).GetOrCreateAsync();

        Assert.NotEqual(firstIdentifier, secondIdentifier);
    }

    // All 128 bits are random. Guid.NewGuid() would fix the version and variant bits, giving every installation's
    // identifier the same shape; over this many fresh databases at least one has to differ in them.
    [Fact]
    public async Task TheIdentifier_LeavesNoBitsFixed()
    {
        var identifiers = new List<Guid>();
        for (var attempt = 0; attempt < 64; attempt++)
        {
            await using var db = CreateContext();
            identifiers.Add(await new LicensingIdentityRepository(db, new FakeTimeProvider(Now)).GetOrCreateAsync());
        }

        Assert.Contains(identifiers, identifier => VersionNibbleOf(identifier) != 4);
        Assert.Contains(identifiers, identifier => (VariantBitsOf(identifier) & 0b1100_0000) != 0b1000_0000);
    }

    // The baseline the system profile is compared against has to be taken at the one moment it can be: when the
    // installation is first seen. A profile captured later has nothing to say about where the installation
    // started.
    [Fact]
    public async Task CreatingTheIdentifier_CapturesTheSystemProfile()
    {
        await using var db = CreateContext();
        var observer = new RecordingSystemProfileObserver();
        var sut = new LicensingIdentityRepository(db, new FakeTimeProvider(Now), observer);

        await sut.GetOrCreateAsync();

        Assert.Equal([Now], observer.Observations);
    }

    [Fact]
    public async Task ReadingAnIdentifierThatAlreadyExists_ObservesNothing()
    {
        await using var db = CreateContext();
        var observer = new RecordingSystemProfileObserver();
        var sut = new LicensingIdentityRepository(db, new FakeTimeProvider(Now), observer);

        await sut.GetOrCreateAsync();
        await sut.GetOrCreateAsync();

        Assert.Single(observer.Observations);
    }

    [Fact]
    public async Task TheCreatedInstant_IsReadableWithoutMintingAnIdentifier()
    {
        await using var db = CreateContext();
        var sut = new LicensingIdentityRepository(db, new FakeTimeProvider(Now));

        Assert.Null(await sut.GetCreatedAtAsync());
        Assert.Equal(0, await db.LicensingIdentity.CountAsync());

        await sut.GetOrCreateAsync();

        Assert.Equal(Now, await sut.GetCreatedAtAsync());
    }

    /// <summary>The version nibble a UUID carries, which is 4 in every value <see cref="Guid.NewGuid" /> returns.</summary>
    private static int VersionNibbleOf(Guid identifier) => identifier.ToByteArray(true)[6] >> 4;

    /// <summary>
    ///     The byte holding the variant bits, whose top two bits are 10 in every value
    ///     <see cref="Guid.NewGuid" /> returns.
    /// </summary>
    private static byte VariantBitsOf(Guid identifier) => identifier.ToByteArray(true)[8];

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_LicensingIdentity_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    /// <summary>Records the instant each observation was asked for, which is what pins capture to creation.</summary>
    private sealed class RecordingSystemProfileObserver : ISystemProfileObserver
    {
        public List<DateTimeOffset> Observations { get; } = [];

        public Task ObserveAsync(DateTimeOffset identityCreatedAt, CancellationToken cancellationToken = default)
        {
            this.Observations.Add(identityCreatedAt);

            return Task.CompletedTask;
        }
    }
}

[Collection("PostgresIntegration")]
public sealed class LicensingIdentityRepositoryPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
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

    // Replicas starting together read for the first time at the same moment. Two rows would make one
    // installation report itself as two, which is the whole thing this identifier exists to prevent.
    [Fact]
    public async Task ConcurrentFirstReads_ProduceOneIdentifier()
    {
        var contexts = Enumerable.Range(0, 6).Select(_ => this.CreatePostgresContext()).ToList();

        Guid[] identifiers;
        try
        {
            identifiers = await Task.WhenAll(
                contexts.Select(context =>
                    new LicensingIdentityRepository(context, new FakeTimeProvider(Now)).GetOrCreateAsync()));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        Assert.Single(identifiers.Distinct());

        await using var verification = this.CreatePostgresContext();
        Assert.Equal(1, await verification.LicensingIdentity.CountAsync());
    }

    // The row exists so a restart reports the same installation, so a new connection must not generate another
    // identifier.
    [Fact]
    public async Task TheIdentifier_SurvivesANewConnection()
    {
        await using var first = this.CreatePostgresContext();
        var original = await new LicensingIdentityRepository(first, new FakeTimeProvider(Now)).GetOrCreateAsync();

        await using var second = this.CreatePostgresContext();
        var reread = await new LicensingIdentityRepository(second, new FakeTimeProvider(Now)).GetOrCreateAsync();

        Assert.Equal(original, reread);
    }

    // Deleting the row is how an installation reports itself as a different one. It has to be safe to do: the
    // license on file is what entitlement rests on, and the identifier binds none of it.
    [Fact]
    public async Task DeletingTheRow_ProducesANewIdentifierAndLeavesTheLicenseOnFile()
    {
        using var chain = LicenseTestChain.Create();
        var compactLicense = chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1)));
        var codec = InstallationLicenseRepositoryTests.CreateCodec();

        await using var db = this.CreatePostgresContext();
        var licenseStore = new InstallationLicenseRepository(db, codec, new FakeTimeProvider(Now));
        await licenseStore.SetAsync(compactLicense, null);

        var sut = new LicensingIdentityRepository(db, new FakeTimeProvider(Now));
        var original = await sut.GetOrCreateAsync();

        await db.LicensingIdentity.ExecuteDeleteAsync();
        var replacement = await sut.GetOrCreateAsync();

        Assert.NotEqual(original, replacement);
        Assert.Equal(1, await db.LicensingIdentity.CountAsync());

        var stored = await licenseStore.GetAsync();
        Assert.NotNull(stored);
        Assert.Equal(compactLicense, stored.CompactLicense);
        Assert.Equal(Now, stored.ActivatedAt);
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreatePostgresContext();
        await db.LicensingIdentity.ExecuteDeleteAsync();
        await db.InstallationLicenses.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
