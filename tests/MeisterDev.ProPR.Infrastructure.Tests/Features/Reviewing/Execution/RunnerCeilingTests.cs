// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Runner enrollment against the licensed runner ceiling, through the registration service and a real gate
///     over a real PostgreSQL instance. The gate decides in the database, so a double would prove nothing about
///     what enrollment does at the ceiling, and nothing about what a refused enrollment costs the token.
///     <para>
///         Every ceiling here is derived from what the installation already holds, so the shared container can
///         carry whatever the rest of the collection has written.
///     </para>
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerCeilingTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    /// <summary>
    ///     Bounds every enrollment the tests make. Without it an enrollment that waited on a lock it should not
    ///     have taken would hang the run instead of failing it.
    /// </summary>
    private static readonly TimeSpan EnrollmentTimeout = TimeSpan.FromSeconds(30);

    private static readonly Guid TenantId = Guid.Parse("7c3f9f4a-4d1e-4f2b-9a6c-5e8d0b1a2c3d");

    private readonly IPasswordHashService _hashes = new PasswordHashService();
    private readonly List<Guid> _runnerIds = [];
    private readonly List<Guid> _tokenIds = [];

    private DbContextOptions<MeisterProPRDbContext> _options = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Removes exactly the rows these tests wrote. The container is shared across the collection, so
    ///     clearing the tables outright would take away state the other tests in it depend on.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        await using var db = this.CreateContext();
        await db.ReviewRunners.Where(runner => this._runnerIds.Contains(runner.Id)).ExecuteDeleteAsync();
        await db.RunnerRegistrationTokens.Where(token => this._tokenIds.Contains(token.Id)).ExecuteDeleteAsync();
    }

    // The refusal is quoted back to whoever started the host, so it names the number the installation is held
    // to and the number it holds. Nothing is written, and the token keeps its unspent use: an enrollment that
    // spent the token to learn the installation was full would leave the operator issuing another one.
    //
    // Two runners are seeded so both numbers are above one, which fixes the plural form the message takes.
    [Fact]
    public async Task RegisterAsync_AtTheLicensedCeiling_IsRefusedAndSpendsNothing()
    {
        this.TrackNew(await this.SeedEnrolledRunnerAsync());
        this.TrackNew(await this.SeedEnrolledRunnerAsync());
        var enrolled = await this.CountEnrolledRunnersAsync();
        var (tokenId, secret) = await this.SeedTokenAsync();

        await using var db = this.CreateContext();
        var result = await this.EnrollAsync(db, LicensedCeilingOf(enrolled), secret);

        Assert.False(result.Succeeded);
        Assert.Equal(
            $"The license in force allows {enrolled} registered runners and {enrolled} hold a current credential. "
            + "Enrolling another requires removing a runner, or a license that allows more.",
            result.Refusal);
        Assert.Equal(0, await this.ReadTokenUseCountAsync(tokenId));
        Assert.Equal(enrolled, await this.CountEnrolledRunnersAsync());
    }

    // Below the ceiling the enrollment and the use it spends are committed rather than left in the admission's
    // transaction, so a reader on another connection sees both and the lock the admission held is released.
    [Fact]
    public async Task RegisterAsync_BelowTheCeiling_CommitsTheEnrollmentAndTheTokenUse()
    {
        var ceiling = await this.CountEnrolledRunnersAsync() + 1;
        var (tokenId, secret) = await this.SeedTokenAsync();

        await using var db = this.CreateContext();
        var result = await this.EnrollAsync(db, LicensedCeilingOf(ceiling), secret);

        Assert.True(result.Succeeded);
        this.TrackNew(result.RunnerId!.Value);

        Assert.Null(db.Database.CurrentTransaction);
        Assert.True(await this.RunnerIsEnrolledAsync(result.RunnerId.Value));
        Assert.Equal(1, await this.ReadTokenUseCountAsync(tokenId));
        Assert.Equal(ceiling, await this.CountEnrolledRunnersAsync());
    }

    // A credential lives only in the runner's own memory, so a host that was restarted or rescaled after its
    // credential expired cannot renew and enrolls again as a new row. The expired row fails authentication and
    // can never be given work, so it does not hold a seat: counting it would let a daily rolling deploy
    // exhaust a licensed fleet long before the prune worker reclaims the rows.
    [Fact]
    public async Task RegisterAsync_WithAnExpiredCredentialOnFile_StillAdmitsTheHostThatEnrollsAgain()
    {
        var ceiling = await this.CountEnrolledRunnersAsync() + 1;
        this.TrackNew(await this.SeedEnrolledRunnerAsync(DateTimeOffset.UtcNow.AddMinutes(-1)));
        var (tokenId, secret) = await this.SeedTokenAsync();

        await using var db = this.CreateContext();
        var result = await this.EnrollAsync(db, LicensedCeilingOf(ceiling), secret);

        Assert.True(result.Succeeded);
        this.TrackNew(result.RunnerId!.Value);
        Assert.Equal(1, await this.ReadTokenUseCountAsync(tokenId));
        Assert.Equal(ceiling, await this.CountEnrolledRunnersAsync());
    }

    // The defect this rules out: a count read before the insert lets two enrollments at the last seat both
    // pass. The gate's own tests prove the arbitration. This one proves enrollment goes through the gate, so
    // the host that loses is refused and its token use is not spent either.
    [Fact]
    public async Task RegisterAsync_FromTwoSimultaneousEnrollmentsAtTheLastSeat_EnrolsExactlyOne()
    {
        // One runner is seeded so the ceiling is above one, which fixes the plural form the refusal takes.
        this.TrackNew(await this.SeedEnrolledRunnerAsync());
        var ceiling = await this.CountEnrolledRunnersAsync() + 1;

        // Two uses, so the token is not what stops the second host.
        var (tokenId, secret) = await this.SeedTokenAsync(maxUses: 2);

        var expectedRefusal =
            $"The license in force allows {ceiling} registered runners and {ceiling} hold a current credential. "
            + "Enrolling another requires removing a runner, or a license that allows more.";

        var outcomes = await this.EnrollSimultaneouslyAsync(LicensedCeilingOf(ceiling), secret);

        var enrolled = Assert.Single(outcomes, outcome => outcome.Succeeded);
        this.TrackNew(enrolled.RunnerId!.Value);

        var refused = Assert.Single(outcomes, outcome => !outcome.Succeeded);
        Assert.Equal(expectedRefusal, refused.Refusal);
        Assert.Equal(ceiling, await this.CountEnrolledRunnersAsync());
        Assert.Equal(1, await this.ReadTokenUseCountAsync(tokenId));
    }

    // The defect this rules out: the token is read, checked, and only then has its use recorded, so two
    // enrollments presenting one single-use token both read a use count of zero and both pass the check. The
    // ceiling leaves room for both hosts, so the token is the only thing that can refuse either, and the
    // admission they queue on serializes them without making either read the token again.
    //
    // The host that loses is told what any other unusable token is worth, and the token is left with the one
    // use it was issued with spent.
    [Fact]
    public async Task RegisterAsync_FromTwoSimultaneousEnrollmentsOnOneSingleUseToken_EnrolsExactlyOne()
    {
        var enrolledBefore = await this.CountEnrolledRunnersAsync();
        var (tokenId, secret) = await this.SeedTokenAsync(maxUses: 1);

        var outcomes = await this.EnrollSimultaneouslyAsync(LicensedCeilingOf(enrolledBefore + 2), secret);

        var enrolled = Assert.Single(outcomes, outcome => outcome.Succeeded);
        this.TrackNew(enrolled.RunnerId!.Value);

        var refused = Assert.Single(outcomes, outcome => !outcome.Succeeded);
        Assert.Equal("The registration token is not valid.", refused.Refusal);
        Assert.Equal(1, await this.ReadTokenUseCountAsync(tokenId));
        Assert.Equal(enrolledBefore + 1, await this.CountEnrolledRunnersAsync());
    }

    // Enrolled rows are counted on every decision, so revoking a runner frees its seat with no further action.
    // The token the refused attempt presented is still unspent, which is why the same one enrolls here.
    [Fact]
    public async Task RegisterAsync_AfterARunnerIsRevoked_IsAdmittedAgain()
    {
        var revocable = this.TrackNew(await this.SeedEnrolledRunnerAsync());
        var ceiling = await this.CountEnrolledRunnersAsync();
        var (_, secret) = await this.SeedTokenAsync();

        await using (var refusing = this.CreateContext())
        {
            Assert.False((await this.EnrollAsync(refusing, LicensedCeilingOf(ceiling), secret)).Succeeded);
        }

        await this.RevokeAsync(revocable);

        await using var admitting = this.CreateContext();
        var result = await this.EnrollAsync(admitting, LicensedCeilingOf(ceiling), secret);

        Assert.True(result.Succeeded);
        this.TrackNew(result.RunnerId!.Value);
        Assert.True(await this.RunnerIsEnrolledAsync(result.RunnerId.Value));
    }

    // A license that sets no runner ceiling admits every enrollment, so nothing is counted and no lock is
    // taken.
    [Fact]
    public async Task RegisterAsync_UnderAnUnlimitedCeiling_Enrols()
    {
        var (_, secret) = await this.SeedTokenAsync();

        await using var db = this.CreateContext();
        var result = await this.EnrollAsync(
            db,
            LicenseLimitResolution.Unlimited(LicenseLimitKey.Runners, LicenseLimitSource.License, LicenseStage.Active),
            secret);

        Assert.True(result.Succeeded);
        this.TrackNew(result.RunnerId!.Value);
        Assert.True(await this.RunnerIsEnrolledAsync(result.RunnerId.Value));
    }

    // A ceiling that drops below what the installation already holds stops new enrollments and revokes nothing.
    // The two numbers in the refusal differ here, so the test can tell them apart. Four runners are seeded so
    // that the lowered ceiling is still above one, which fixes the plural form the message takes.
    [Fact]
    public async Task RegisterAsync_WithMoreRunnersThanALoweredCeilingAllows_KeepsThemAndRefusesTheNextOne()
    {
        var seeded = new List<Guid>
        {
            this.TrackNew(await this.SeedEnrolledRunnerAsync()),
            this.TrackNew(await this.SeedEnrolledRunnerAsync()),
            this.TrackNew(await this.SeedEnrolledRunnerAsync()),
            this.TrackNew(await this.SeedEnrolledRunnerAsync()),
        };
        var enrolled = await this.CountEnrolledRunnersAsync();
        var ceiling = enrolled - 2;
        var (_, secret) = await this.SeedTokenAsync();

        await using var db = this.CreateContext();
        var result = await this.EnrollAsync(db, LicensedCeilingOf(ceiling), secret);

        Assert.False(result.Succeeded);
        Assert.Equal(
            $"The license in force allows {ceiling} registered runners and {enrolled} hold a current credential. "
            + "Enrolling another requires removing a runner, or a license that allows more.",
            result.Refusal);

        await using var read = this.CreateContext();
        Assert.Equal(
            seeded.Count,
            await read.ReviewRunners.CountAsync(runner =>
                seeded.Contains(runner.Id) && runner.State == RunnerState.Enrolled));
    }

    private static LicenseLimitResolution LicensedCeilingOf(long ceiling) =>
        LicenseLimitResolution.Of(LicenseLimitKey.Runners, ceiling, LicenseLimitSource.License, LicenseStage.Active);

    /// <summary>
    ///     Enrolls a host through the registration service over one context, with the product's own gate
    ///     deciding against a fixed ceiling. The gate opens its transaction on this context, which is the one
    ///     the registry saves the enrollment and the token use through.
    /// </summary>
    private async Task<RunnerRegistrationResult> EnrollAsync(
        MeisterProPRDbContext db,
        LicenseLimitResolution limit,
        string registrationToken)
    {
        var limits = Substitute.For<ILicenseLimitResolver>();
        limits.ResolveAsync(limit.Key, Arg.Any<CancellationToken>()).Returns(limit);

        var service = new RunnerRegistrationService(
            new RunnerRegistry(db),
            this._hashes,
            TimeProvider.System,
            NullLogger<RunnerRegistrationService>.Instance,
            licensing: null,
            new PostgresStockQuotaGate(db, limits, TimeProvider.System));

        using var timeout = new CancellationTokenSource(EnrollmentTimeout);
        return await service.RegisterAsync(
            new RunnerRegistrationRequest(
                registrationToken,
                $"runner-ceiling-{Guid.NewGuid():N}",
                [],
                RunnerContractVersion.Current),
            timeout.Token);
    }

    /// <summary>
    ///     Enrolls two hosts at once, each over its own context. Both report themselves ready and then wait for
    ///     one signal, so they ask for the admission together. Starting them one after another would let the
    ///     first finish before the second began.
    /// </summary>
    /// <param name="limit">The ceiling both enrollments are decided against.</param>
    /// <param name="registrationToken">The token both hosts present.</param>
    /// <returns>What each host was told, in no particular order.</returns>
    private async Task<IReadOnlyList<RunnerRegistrationResult>> EnrollSimultaneouslyAsync(
        LicenseLimitResolution limit,
        string registrationToken)
    {
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                allArrived.SetResult();
            }

            await release.Task;

            await using var db = this.CreateContext();
            return await this.EnrollAsync(db, limit, registrationToken);
        })).ToList();

        await allArrived.Task.WaitAsync(EnrollmentTimeout);
        release.SetResult();
        return await Task.WhenAll(attempts);
    }

    private async Task<long> CountEnrolledRunnersAsync()
    {
        await using var db = this.CreateContext();
        return (await new LicensedResourceCountRepository(db, TimeProvider.System).GetCountsAsync()).EnrolledRunners;
    }

    private async Task<bool> RunnerIsEnrolledAsync(Guid runnerId)
    {
        await using var db = this.CreateContext();
        return await db.ReviewRunners
            .AsNoTracking()
            .AnyAsync(runner => runner.Id == runnerId && runner.State == RunnerState.Enrolled);
    }

    private async Task<int> ReadTokenUseCountAsync(Guid tokenId)
    {
        await using var db = this.CreateContext();
        return await db.RunnerRegistrationTokens
            .AsNoTracking()
            .Where(token => token.Id == tokenId)
            .Select(token => token.UseCount)
            .SingleAsync();
    }

    /// <summary>
    ///     A runner written straight to the table. Enrolling one through the service would need an admission of
    ///     its own, and these tests measure the admission.
    /// </summary>
    private async Task<Guid> SeedEnrolledRunnerAsync(DateTimeOffset? credentialExpiresAt = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = this.CreateContext();
        db.ReviewRunners.Add(
            new ReviewRunner(
                id,
                TenantId,
                $"runner-ceiling-seed-{id:N}",
                [],
                RunnerContractVersion.Current,
                $"hash-{id:N}",
                $"lookup-{id:N}",
                credentialExpiresAt ?? now.AddDays(30),
                now));
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Issues a registration token and returns the secret a host presents to enroll with it.</summary>
    private async Task<(Guid TokenId, string Secret)> SeedTokenAsync(int maxUses = 1)
    {
        var secret = $"runner-ceiling-token-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var token = new RunnerRegistrationToken(
            Guid.NewGuid(),
            TenantId,
            [],
            this._hashes.Hash(secret),
            PatTokenLookupHash.Compute(secret),
            now,
            now.AddHours(1),
            maxUses,
            Guid.NewGuid());

        await using var db = this.CreateContext();
        db.RunnerRegistrationTokens.Add(token);
        await db.SaveChangesAsync();
        this._tokenIds.Add(token.Id);
        return (token.Id, secret);
    }

    private async Task RevokeAsync(Guid runnerId)
    {
        await using var db = this.CreateContext();
        var runner = await db.ReviewRunners.SingleAsync(candidate => candidate.Id == runnerId);
        runner.Revoke(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    /// <summary>Records an identifier for removal and returns it, so a failed test leaves no rows behind.</summary>
    private Guid TrackNew(Guid id)
    {
        this._runnerIds.Add(id);
        return id;
    }

    private MeisterProPRDbContext CreateContext() => new(this._options);
}
