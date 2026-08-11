// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerRegistrationServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ScopedClient = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherClient = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IRunnerRegistry _registry = Substitute.For<IRunnerRegistry>();
    private readonly IPasswordHashService _hashes = new PassThroughHashService();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

    private ReviewRunner? _added;

    private RunnerRegistrationService CreateService()
    {
        this._registry.AddAsync(Arg.Any<ReviewRunner>(), Arg.Any<RunnerRegistrationToken>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                this._added = call.ArgAt<ReviewRunner>(0);
                return Task.CompletedTask;
            });

        return new RunnerRegistrationService(
            this._registry,
            this._hashes,
            this._time,
            NullLogger<RunnerRegistrationService>.Instance);
    }

    private RunnerRegistrationToken IssueToken(
        string raw = "reg-token",
        int maxUses = 1,
        int expiresInHours = 24,
        Guid? tenantId = null,
        params Guid[] scope)
    {
        var token = new RunnerRegistrationToken(
            Guid.NewGuid(),
            tenantId ?? TenantId,
            scope.Length == 0 ? [ScopedClient] : scope,
            this._hashes.Hash(raw),
            PatTokenLookupHash.Compute(raw),
            this._time.GetUtcNow(),
            this._time.GetUtcNow().AddHours(expiresInHours),
            maxUses,
            Guid.NewGuid());

        this._registry.FindTokenAsync(PatTokenLookupHash.Compute(raw), Arg.Any<CancellationToken>())
            .Returns(token);
        return token;
    }

    private static RunnerRegistrationRequest Request(string token = "reg-token", params string[] tags)
    {
        return new RunnerRegistrationRequest(token, "runner-01", tags, RunnerContractVersion.Current);
    }

    // A runner enrolled in the System tenant is offered every tenant's work, so its credential opens every
    // tenant's source rather than one customer's. It is bounded far shorter for that reason; a running host
    // renews an hour before expiry and never notices the difference.
    [Fact]
    public async Task ASharedRunnersCredential_ExpiresSoonerThanATenantScopedOne()
    {
        this.IssueToken(tenantId: SystemTenant.Id);
        await this.CreateService().RegisterAsync(Request());
        var sharedExpiry = this._added!.CredentialExpiresAt;

        this._added = null;
        this.IssueToken();
        await this.CreateService().RegisterAsync(Request());
        var tenantScopedExpiry = this._added!.CredentialExpiresAt;

        Assert.True(
            sharedExpiry < tenantScopedExpiry,
            $"a shared runner's credential ({sharedExpiry}) should expire before a tenant-scoped one's ({tenantScopedExpiry})");
    }

    [Fact]
    public async Task AValidToken_EnrolsTheRunnerAndReturnsItsCredentialOnce()
    {
        this.IssueToken();

        var result = await this.CreateService().RegisterAsync(Request());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Credential);
        Assert.NotEmpty(result.Credential!);
        Assert.NotNull(this._added);
        Assert.Equal(RunnerState.Enrolled, this._added!.State);
    }

    // The rule everything else rests on: scope comes from the operator's token, and the request has
    // nowhere to put one. A runner that could name its clients would make isolation a matter of
    // configuration discipline.
    [Fact]
    public async Task TheClientScope_ComesFromTheTokenAndNotFromTheRunner()
    {
        this.IssueToken(scope: [ScopedClient]);

        await this.CreateService().RegisterAsync(Request());

        Assert.Equal([ScopedClient], this._added!.ClientScope);
        Assert.True(this._added.CoversClient(ScopedClient));
        Assert.False(this._added.CoversClient(OtherClient));
    }

    [Fact]
    public async Task TagsAreTakenFromTheRunner_AndCannotReachTheScope()
    {
        this.IssueToken(scope: [ScopedClient]);

        await this.CreateService().RegisterAsync(Request(tags: ["linux", "gpu"]));

        Assert.Equal(["linux", "gpu"], this._added!.Tags);
        Assert.Equal([ScopedClient], this._added.ClientScope);
    }

    [Fact]
    public async Task RegistrationWithoutAToken_IsRefused()
    {
        this._registry.FindTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RunnerRegistrationToken?)null);

        var result = await this.CreateService().RegisterAsync(Request("nonsense"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
    {
        this.IssueToken(expiresInHours: 1);
        this._time.Advance(TimeSpan.FromHours(2));

        Assert.False((await this.CreateService().RegisterAsync(Request())).Succeeded);
    }

    // Bounded uses are what limit a leaked token: it enrolls the hosts it was meant for and then nothing.
    [Fact]
    public async Task ATokenBeyondItsUseCount_IsRefused()
    {
        var token = this.IssueToken(maxUses: 1);
        var service = this.CreateService();

        Assert.True((await service.RegisterAsync(Request())).Succeeded);
        Assert.Equal(1, token.UseCount);
        Assert.False((await service.RegisterAsync(Request())).Succeeded);
    }

    [Fact]
    public async Task ARevokedToken_IsRefused()
    {
        var token = this.IssueToken();
        token.Revoke(this._time.GetUtcNow());

        Assert.False((await this.CreateService().RegisterAsync(Request())).Succeeded);
    }

    [Fact]
    public async Task ARunnerSpeakingAnUnsupportedContract_IsRefusedNamingBothVersions()
    {
        this.IssueToken();

        var result = await this.CreateService().RegisterAsync(new RunnerRegistrationRequest("reg-token", "runner-01", [], RunnerContractVersion.Current + 5));

        Assert.False(result.Succeeded);
        Assert.Contains(RunnerContractVersion.Current.ToString(), result.Refusal!, StringComparison.Ordinal);
    }

    // Renewal exists so a credential can expire without an operator enrolling the host again, which means it
    // must not re-stamp a scope the operator has since changed.
    [Fact]
    public async Task RenewingACredential_KeepsTheIdentityAndTheScope()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("old-secret"), PatTokenLookupHash.Compute("old-secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var result = await this.CreateService()
            .RenewCredentialAsync(runner.Id, "old-secret", RunnerContractVersion.Current);

        Assert.True(result.Succeeded);
        Assert.Equal(runner.Id, result.RunnerId);
        Assert.Equal([ScopedClient], runner.ClientScope);
        Assert.NotEqual("old-secret", result.Credential);
    }

    [Fact]
    public async Task RenewingWithTheWrongCredential_IsRefused()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("old-secret"), PatTokenLookupHash.Compute("old-secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        Assert.False(
            (await this.CreateService()
                .RenewCredentialAsync(runner.Id, "guessed", RunnerContractVersion.Current)).Succeeded);
    }

    // Last-seen is the only evidence an operator has that a runner is alive, so it has to survive the
    // request that observed it. Mutating the entity and not writing it leaves the column permanently null.
    [Fact]
    public async Task AuthenticatingARunner_PersistsWhenItWasLastSeen()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("secret"), PatTokenLookupHash.Compute("secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByCredentialLookupAsync(PatTokenLookupHash.Compute("secret"), Arg.Any<CancellationToken>())
            .Returns(runner);

        Assert.NotNull(await this.CreateService().AuthenticateAsync("secret"));

        Assert.Equal(this._time.GetUtcNow(), runner.LastSeenAt);
        await this._registry.Received(1).UpdateAsync(runner, Arg.Any<CancellationToken>());
    }

    // A runner authenticates once per proxied call, and several files review at once, so writing on every
    // call would put a liveness field on the hot path without recording anything an operator can use.
    [Fact]
    public async Task AuthenticatingRepeatedly_DoesNotWriteLastSeenEveryTime()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("secret"), PatTokenLookupHash.Compute("secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByCredentialLookupAsync(PatTokenLookupHash.Compute("secret"), Arg.Any<CancellationToken>())
            .Returns(runner);
        var service = this.CreateService();

        await service.AuthenticateAsync("secret");
        this._time.Advance(TimeSpan.FromSeconds(5));
        await service.AuthenticateAsync("secret");
        await service.AuthenticateAsync("secret");

        await this._registry.Received(1).UpdateAsync(runner, Arg.Any<CancellationToken>());

        // Past the resolution the next call records a fresh time, so a runner that goes quiet still shows it.
        this._time.Advance(TimeSpan.FromMinutes(2));
        await service.AuthenticateAsync("secret");

        Assert.Equal(this._time.GetUtcNow(), runner.LastSeenAt);
        await this._registry.Received(2).UpdateAsync(runner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARevokedRunner_NoLongerAuthenticates()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("secret"), PatTokenLookupHash.Compute("secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByCredentialLookupAsync(PatTokenLookupHash.Compute("secret"), Arg.Any<CancellationToken>())
            .Returns(runner);
        this._registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);
        var service = this.CreateService();

        Assert.NotNull(await service.AuthenticateAsync("secret"));
        Assert.True(await service.RevokeAsync(runner.Id));
        Assert.Null(await service.AuthenticateAsync("secret"));
    }

    [Fact]
    public async Task AnExpiredCredential_NoLongerAuthenticates()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("secret"), PatTokenLookupHash.Compute("secret"),
            this._time.GetUtcNow().AddHours(1), this._time.GetUtcNow());
        this._registry.FindByCredentialLookupAsync(PatTokenLookupHash.Compute("secret"), Arg.Any<CancellationToken>())
            .Returns(runner);
        var service = this.CreateService();

        Assert.NotNull(await service.AuthenticateAsync("secret"));
        this._time.Advance(TimeSpan.FromHours(2));
        Assert.Null(await service.AuthenticateAsync("secret"));
    }

    [Fact]
    public async Task TwoEnrolments_GetDifferentCredentials()
    {
        this.IssueToken(maxUses: 2);
        var service = this.CreateService();

        var first = await service.RegisterAsync(Request());
        var second = await service.RegisterAsync(Request());

        Assert.NotEqual(first.Credential, second.Credential);
    }

    // The stale-row case this exists for: a redeployed host re-enrolled as a new identity, and the old row
    // stayed in the registry, counting as capacity and shown as unreachable in the fleet view.
    [Fact]
    public async Task DeletingAnIdleRunner_RemovesItsRow()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("secret"), PatTokenLookupHash.Compute("secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);
        this._registry.HoldsLeaseAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(false);
        this._registry.DeleteAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(true);

        var outcome = await this.CreateService().DeleteAsync(runner.Id);

        Assert.Equal(RunnerDeletionOutcome.Deleted, outcome);
        await this._registry.Received(1).DeleteAsync(runner.Id, Arg.Any<CancellationToken>());
    }

    // A held lease is still being renewed against this identity. Deleting it under a running job would
    // orphan work the lease machinery is still tracking, so the honest sequence is revoke, wait, delete.
    [Fact]
    public async Task DeletingARunnerHoldingALease_IsRefusedWithoutTouchingTheRow()
    {
        var runner = new ReviewRunner(
            Guid.NewGuid(), TenantId, "runner-01", [ScopedClient], RunnerContractVersion.Current,
            this._hashes.Hash("secret"), PatTokenLookupHash.Compute("secret"),
            this._time.GetUtcNow().AddDays(1), this._time.GetUtcNow());
        this._registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);
        this._registry.HoldsLeaseAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(true);

        var outcome = await this.CreateService().DeleteAsync(runner.Id);

        Assert.Equal(RunnerDeletionOutcome.HoldingLease, outcome);
        await this._registry.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletingAnUnknownRunner_SaysNotFound()
    {
        this._registry.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ReviewRunner?)null);

        Assert.Equal(RunnerDeletionOutcome.NotFound, await this.CreateService().DeleteAsync(Guid.NewGuid()));
    }
}
