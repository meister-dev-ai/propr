// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Budgeting;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Integration tests for <see cref="BudgetCapsProvider" /> against a real PostgreSQL instance, covering the
///     Budgeting license gate and the manual-reset allowance folded into the monthly caps.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class BudgetCapsProviderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    /// <summary>
    ///     A fixed "now" shared by the provider and the fixtures, so a run that straddles a UTC month boundary
    ///     cannot flip which period a reset belongs to.
    /// </summary>
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 9, 14, 0, TimeSpan.Zero);

    private Guid _clientId;
    private MeisterProPRDbContext _dbContext = null!;
    private TestDbContextFactory _factory = null!;
    private BudgetSpendResetRepository _resetRepository = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._factory = new TestDbContextFactory(options);
        this._resetRepository = new BudgetSpendResetRepository(this._factory);

        this._clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Budget Caps Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                MonthlyBudgetHardCapUsd = 100m,
            });
        await this._dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.BudgetSpendResets.Where(r => r.ClientId == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task GetCapsAsync_ReturnsConfiguredCaps_WhenBudgetingIsLicensed()
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.Budgeting, Arg.Any<CancellationToken>()).Returns(true);
        var provider = this.CreateProvider(licensing);

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.True(caps.AnyConfigured);
        Assert.Equal(100m, caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetCapsAsync_ReturnsNone_WhenBudgetingIsNotLicensed()
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.Budgeting, Arg.Any<CancellationToken>()).Returns(false);
        var provider = this.CreateProvider(licensing);

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.False(caps.AnyConfigured);
        Assert.Null(caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetCapsAsync_ReadsConfiguredCaps_WhenNoLicensingServiceIsRegistered()
    {
        var provider = this.CreateProvider();

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.Equal(100m, caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetCapsAsync_AddsTheAllowanceGrantedByAResetInTheCurrentPeriod()
    {
        await this.GrantResetAsync(Now.UtcDateTime, topUpHardUsd: 100m);
        var provider = this.CreateProvider();

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.Equal(200m, caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetCapsAsync_IgnoresAnAllowanceGrantedInAnotherPeriod()
    {
        await this.GrantResetAsync(Now.UtcDateTime.AddMonths(-1), topUpHardUsd: 100m);
        var provider = this.CreateProvider();

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.Equal(100m, caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetConfiguredCapsAsync_ExcludesAnyGrantedAllowance()
    {
        await this.GrantResetAsync(Now.UtcDateTime, topUpHardUsd: 100m);
        var provider = this.CreateProvider();

        var caps = await provider.GetConfiguredCapsAsync(this._clientId);

        Assert.Equal(100m, caps.MonthlyHardCapUsd);
    }

    private BudgetCapsProvider CreateProvider(ILicensingCapabilityService? licensing = null) =>
        new(this._factory, this._resetRepository, new FixedTimeProvider(Now), licensing);

    private async Task GrantResetAsync(DateTime performedAt, decimal topUpHardUsd)
    {
        var utc = performedAt.ToUniversalTime();
        await this._resetRepository.AddAsync(
            new BudgetSpendReset
            {
                Id = Guid.NewGuid(),
                ClientId = this._clientId,
                PeriodStart = new DateOnly(utc.Year, utc.Month, 1),
                TopUpHardCapUsd = topUpHardUsd,
                EffectiveHardCapBeforeUsd = 100m,
                EffectiveHardCapAfterUsd = 100m + topUpHardUsd,
                PerformedAt = utc,
            },
            CancellationToken.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
