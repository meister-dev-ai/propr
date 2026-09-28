// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Covers how a job finds the reasoning-capture policy it must honour. A job carries a client, not a tenant,
///     so the lookup walks the client to its tenant. A walk that comes up empty leaves the installation switch
///     in charge; a walk that faults captures nothing, because no tenant has permitted the text.
/// </summary>
public sealed class TenantReasoningCapturePolicyProviderTests
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid ClientId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Theory]
    [InlineData(ReasoningCapturePolicy.Enabled)]
    [InlineData(ReasoningCapturePolicy.Disabled)]
    [InlineData(ReasoningCapturePolicy.InstallationDefault)]
    public async Task TheClientAnswersWithItsOwningTenantsPolicy(ReasoningCapturePolicy stored)
    {
        var sut = Sut(stored);

        Assert.Equal(stored, await sut.GetForClientAsync(ClientId));
    }

    [Fact]
    public async Task EveryClientOfTheSameTenantAnswersWithTheSamePolicy()
    {
        var secondClientId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var sut = Sut(ReasoningCapturePolicy.Disabled, additionalClientId: secondClientId);

        Assert.Equal(ReasoningCapturePolicy.Disabled, await sut.GetForClientAsync(ClientId));
        Assert.Equal(ReasoningCapturePolicy.Disabled, await sut.GetForClientAsync(secondClientId));
    }

    [Fact]
    public async Task AClientNobodyOwnsLeavesTheInstallationSwitchInCharge()
    {
        var sut = Sut(ReasoningCapturePolicy.Disabled);

        Assert.Equal(ReasoningCapturePolicy.InstallationDefault, await sut.GetForClientAsync(Guid.NewGuid()));
        Assert.Equal(ReasoningCapturePolicy.InstallationDefault, await sut.GetForClientAsync(Guid.Empty));
    }

    // The System tenant has no editable policy surface, so whatever its row holds it reads as the installation
    // default.
    [Fact]
    public async Task AClientOfTheSystemTenantLeavesTheInstallationSwitchInCharge()
    {
        var systemClientId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        var sut = Sut(ReasoningCapturePolicy.Disabled, systemTenantClientId: systemClientId);

        Assert.Equal(ReasoningCapturePolicy.InstallationDefault, await sut.GetForClientAsync(systemClientId));
    }

    // The lookup opens one context, and reads the client and its tenant together inside it, so the answer
    // describes one state of the store: a client reassigned between two reads would otherwise be answered
    // with the policy of a tenant it no longer belongs to.
    [Fact]
    public async Task ThePolicyIsReadThroughOneContext()
    {
        var factory = new CountingDbContextFactory(SeededOptions(ReasoningCapturePolicy.Disabled, null, null));
        var sut = new TenantReasoningCapturePolicyProvider(
            factory,
            NullLogger<TenantReasoningCapturePolicyProvider>.Instance);

        Assert.Equal(ReasoningCapturePolicy.Disabled, await sut.GetForClientAsync(ClientId));
        Assert.Equal(1, factory.Created);
    }

    // A call naming no client at all is answered before the store is touched.
    [Fact]
    public async Task AnEmptyClientIdReachesNoStore()
    {
        var factory = new CountingDbContextFactory(SeededOptions(ReasoningCapturePolicy.Disabled, null, null));
        var sut = new TenantReasoningCapturePolicyProvider(
            factory,
            NullLogger<TenantReasoningCapturePolicyProvider>.Instance);

        Assert.Equal(ReasoningCapturePolicy.InstallationDefault, await sut.GetForClientAsync(Guid.Empty));
        Assert.Equal(0, factory.Created);
    }

    // A lookup that throws must not stop a review, and must not record reasoning either: the installation
    // switch defaults to capturing, so falling back to it would record the text of a tenant that turned
    // capture off in the one case the policy exists for.
    [Fact]
    public async Task AFailedLookupCapturesNoReasoning()
    {
        var sut = new TenantReasoningCapturePolicyProvider(
            new ThrowingDbContextFactory(),
            NullLogger<TenantReasoningCapturePolicyProvider>.Instance);

        Assert.Equal(ReasoningCapturePolicy.Disabled, await sut.GetForClientAsync(ClientId));
    }

    private static TenantReasoningCapturePolicyProvider Sut(
        ReasoningCapturePolicy storedPolicy,
        Guid? additionalClientId = null,
        Guid? systemTenantClientId = null)
    {
        return new TenantReasoningCapturePolicyProvider(
            new TestDbContextFactory(SeededOptions(storedPolicy, additionalClientId, systemTenantClientId)),
            NullLogger<TenantReasoningCapturePolicyProvider>.Instance);
    }

    private static DbContextOptions<MeisterProPRDbContext> SeededOptions(
        ReasoningCapturePolicy storedPolicy,
        Guid? additionalClientId,
        Guid? systemTenantClientId)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seed = new MeisterProPRDbContext(options))
        {
            seed.Tenants.Add(NewTenant(TenantId, "acme", storedPolicy));
            seed.Clients.Add(NewClient(ClientId, TenantId));

            if (additionalClientId is { } secondClientId)
            {
                seed.Clients.Add(NewClient(secondClientId, TenantId));
            }

            if (systemTenantClientId is { } systemClientId)
            {
                seed.Tenants.Add(NewTenant(TenantCatalog.SystemTenantId, TenantCatalog.SystemTenantSlug, ReasoningCapturePolicy.Disabled));
                seed.Clients.Add(NewClient(systemClientId, TenantCatalog.SystemTenantId));
            }

            seed.SaveChanges();
        }

        return options;
    }

    private static TenantRecord NewTenant(Guid tenantId, string slug, ReasoningCapturePolicy policy)
    {
        return new TenantRecord
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = slug,
            IsActive = true,
            LocalLoginEnabled = true,
            ReasoningCapturePolicy = policy,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ClientRecord NewClient(Guid clientId, Guid tenantId)
    {
        return new ClientRecord
        {
            Id = clientId,
            TenantId = tenantId,
            DisplayName = $"Client {clientId}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Counts the contexts a lookup opens, whichever of the two factory methods it calls.</summary>
    private sealed class CountingDbContextFactory(DbContextOptions<MeisterProPRDbContext> options)
        : IDbContextFactory<MeisterProPRDbContext>
    {
        public int Created { get; private set; }

        public MeisterProPRDbContext CreateDbContext()
        {
            this.Created++;
            return new MeisterProPRDbContext(options);
        }

        public Task<MeisterProPRDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            return Task.FromResult(this.CreateDbContext());
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<MeisterProPRDbContext>
    {
        public MeisterProPRDbContext CreateDbContext()
        {
            throw new InvalidOperationException("The tenant store is unreachable.");
        }

        public Task<MeisterProPRDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            throw new InvalidOperationException("The tenant store is unreachable.");
        }
    }
}
