// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Budgeting;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Features.Budgeting;

/// <summary>
///     Integration tests for <see cref="BudgetCapsProvider" /> against a real PostgreSQL instance, covering the
///     Budgeting license gate.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class BudgetCapsProviderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private Guid _clientId;
    private MeisterProPRDbContext _dbContext = null!;
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._factory = new TestDbContextFactory(options);

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

        await this._dbContext.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task GetCapsAsync_ReturnsConfiguredCaps_WhenBudgetingIsLicensed()
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.Budgeting, Arg.Any<CancellationToken>()).Returns(true);
        var provider = new BudgetCapsProvider(this._factory, licensing);

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.True(caps.AnyConfigured);
        Assert.Equal(100m, caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetCapsAsync_ReturnsNone_WhenBudgetingIsNotLicensed()
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.Budgeting, Arg.Any<CancellationToken>()).Returns(false);
        var provider = new BudgetCapsProvider(this._factory, licensing);

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.False(caps.AnyConfigured);
        Assert.Null(caps.MonthlyHardCapUsd);
    }

    [Fact]
    public async Task GetCapsAsync_ReadsConfiguredCaps_WhenNoLicensingServiceIsRegistered()
    {
        var provider = new BudgetCapsProvider(this._factory);

        var caps = await provider.GetCapsAsync(this._clientId);

        Assert.Equal(100m, caps.MonthlyHardCapUsd);
    }
}
