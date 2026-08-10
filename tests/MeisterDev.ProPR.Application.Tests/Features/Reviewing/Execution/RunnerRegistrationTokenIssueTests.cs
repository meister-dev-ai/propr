// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Issuing an enrollment token and re-scoping a runner: the two operator actions that decide what a
///     compromised runner would reach.
/// </summary>
public sealed class RunnerRegistrationTokenIssueTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IRunnerRegistry _registry = Substitute.For<IRunnerRegistry>();
    private readonly IPasswordHashService _hashes = new PassThroughHashService();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero));

    // The secret is returned once and never stored in a readable form. An operator who loses it issues
    // another; there is deliberately no way to read this one back.
    [Fact]
    public async Task AnIssuedToken_IsStoredOnlyAsHashes()
    {
        RunnerRegistrationToken? persisted = null;
        await this._registry.AddTokenAsync(Arg.Do<RunnerRegistrationToken>(t => persisted = t), Arg.Any<CancellationToken>());

        var issue = await this.CreateService().IssueRegistrationTokenAsync(TenantId, [ClientA], TimeSpan.FromHours(12), Guid.NewGuid());

        Assert.NotNull(persisted);
        Assert.NotEmpty(issue.Token);

        // The stored value went through the hasher rather than being written as-is. Asserted as "equals
        // what the hasher produces" rather than "does not contain the secret", because the test double
        // deliberately embeds the plaintext so Verify can work, and the weaker phrasing would pass on a
        // service that stored the raw token.
        Assert.Equal(this._hashes.Hash(issue.Token), persisted!.TokenHash);
        Assert.NotEqual(issue.Token, persisted.TokenHash);

        // The lookup hash is the real one-way hash, so here the secret genuinely must not appear.
        Assert.Equal(PatTokenLookupHash.Compute(issue.Token), persisted.TokenLookupHash);
        Assert.DoesNotContain(issue.Token, persisted.TokenLookupHash, StringComparison.Ordinal);
    }

    // Single use unless asked otherwise. An enrollment secret that enrolls an unbounded number of hosts is
    // a larger thing to lose than one that enrolls a single host, so the caller has to say it wants more.
    [Fact]
    public async Task AnIssuedToken_EnrollsExactlyOneRunnerByDefault()
    {
        RunnerRegistrationToken? persisted = null;
        await this._registry.AddTokenAsync(Arg.Do<RunnerRegistrationToken>(t => persisted = t), Arg.Any<CancellationToken>());

        await this.CreateService().IssueRegistrationTokenAsync(TenantId, [], TimeSpan.FromHours(1), Guid.NewGuid());

        Assert.Equal(1, persisted!.MaxUses);
    }

    // A scaling group's replicas start without an operator present to issue each of them a token, so one
    // token enrolls several hosts. The count is bounded so the remaining uses stay visible.
    [Fact]
    public async Task AnIssuedToken_CarriesTheEnrollmentCountTheOperatorChose()
    {
        RunnerRegistrationToken? persisted = null;
        await this._registry.AddTokenAsync(Arg.Do<RunnerRegistrationToken>(t => persisted = t), Arg.Any<CancellationToken>());

        await this.CreateService().IssueRegistrationTokenAsync(TenantId, [], TimeSpan.FromHours(1), Guid.NewGuid(), 20);

        Assert.Equal(20, persisted!.MaxUses);
    }

    [Fact]
    public async Task ATokenThatEnrollsNobody_IsRefusedRatherThanMinted()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            this.CreateService().IssueRegistrationTokenAsync(TenantId, [], TimeSpan.FromHours(1), Guid.NewGuid(), 0));
    }

    // A key a scaling group reads from its secret store outlives any lifetime an operator would want to
    // keep renewing, so "no expiry" and "no limit" are choices the issuing path has to be able to record.
    [Fact]
    public async Task AnIssuedToken_MayHaveNeitherAnExpiryNorAUseLimit()
    {
        RunnerRegistrationToken? persisted = null;
        await this._registry.AddTokenAsync(Arg.Do<RunnerRegistrationToken>(t => persisted = t), Arg.Any<CancellationToken>());

        var issue = await this.CreateService()
            .IssueRegistrationTokenAsync(TenantId, [], null, Guid.NewGuid(), null);

        Assert.Null(persisted!.ExpiresAt);
        Assert.Null(persisted.MaxUses);
        Assert.Null(issue.ExpiresAt);

        // Unbounded is not the same as unusable: it stays usable however far the clock is wound on, and
        // however many hosts have already enrolled with it. Revocation is the only thing that stops it.
        Assert.True(persisted.IsUsableAt(this._time.GetUtcNow().AddYears(50)));
        persisted.RecordUse();
        persisted.RecordUse();
        Assert.True(persisted.IsUsableAt(this._time.GetUtcNow()));

        persisted.Revoke(this._time.GetUtcNow());
        Assert.False(persisted.IsUsableAt(this._time.GetUtcNow()));
    }

    [Fact]
    public async Task AnIssuedToken_CarriesTheScopeTheOperatorChose()
    {
        RunnerRegistrationToken? persisted = null;
        await this._registry.AddTokenAsync(Arg.Do<RunnerRegistrationToken>(t => persisted = t), Arg.Any<CancellationToken>());

        var issue = await this.CreateService().IssueRegistrationTokenAsync(TenantId, [ClientA], TimeSpan.FromHours(6), Guid.NewGuid());

        Assert.Equal([ClientA], persisted!.ClientScope);
        Assert.Equal(TenantId, persisted.TenantId);
        Assert.Equal(this._time.GetUtcNow().AddHours(6), issue.ExpiresAt);
    }

    // Re-scoping decides what a runner is offered next and deliberately does nothing to the lease it holds:
    // narrowing a scope must not abandon a review that is already half-finished.
    [Fact]
    public async Task ReScopingARunner_ChangesWhatItIsOfferedNextAndNothingElse()
    {
        var runner = MakeRunner();
        this._registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        Assert.True(await this.CreateService().AssignClientScopeAsync(runner.Id, [ClientA]));

        Assert.Equal([ClientA], runner.ClientScope);
        Assert.Equal(RunnerState.Enrolled, runner.State);
        await this._registry.Received(1).UpdateAsync(runner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReScopingARunnerThatIsNotThere_ReportsItRatherThanThrowing()
    {
        this._registry.FindByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ReviewRunner?)null);

        Assert.False(await this.CreateService().AssignClientScopeAsync(Guid.NewGuid(), [ClientA]));
    }

    private static ReviewRunner MakeRunner()
    {
        return new ReviewRunner(
            Guid.NewGuid(),
            TenantId,
            "runner-01",
            [],
            RunnerContractVersion.Current,
            "hashed:secret",
            "LOOKUP",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private RunnerRegistrationService CreateService()
    {
        return new RunnerRegistrationService(
            this._registry,
            this._hashes,
            this._time,
            NullLogger<RunnerRegistrationService>.Instance);
    }
}
