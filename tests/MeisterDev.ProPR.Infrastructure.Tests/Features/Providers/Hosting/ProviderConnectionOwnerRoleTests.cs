// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     Whether one administrator still holds the role a connection's owner requires, read where there is no
///     request to read it from.
/// </summary>
/// <remarks>
///     This is the check a credential store makes, minutes after the request that started the flow has gone. It
///     answers the same question the dispatch route answers from the caller's resolved roles, so an
///     administrator the route admits is one this admits and the other way round.
/// </remarks>
[Collection("PostgresIntegration")]
public sealed class ProviderConnectionOwnerRoleTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private ProviderHostPrimitiveHarness _harness = null!;
    private ProviderConnectionOwnerRoles _ownerRoles = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._harness = ProviderHostPrimitiveHarness.Create(fixture.ConnectionString);
        this._ownerRoles = new ProviderConnectionOwnerRoles(this._harness.Contexts);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (fixture.IsAvailable)
        {
            await this._harness.DisposeAsync();
        }
    }

    // A member of the tenant rather than an administrator of it, so the client assignment is the whole of what
    // this account holds.
    [Fact]
    public async Task AnExplicitClientAdministratorOfTheOwningClientHoldsTheRole()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var client = await this._harness.SeedClientAsync(tenant);
        var administrator = await this._harness.SeedAdministratorAsync(
            tenantId: tenant,
            tenantRole: TenantRole.TenantUser);
        await this._harness.SeedClientAssignmentAsync(client, administrator);

        Assert.True(await this._ownerRoles.HoldsOwnerRoleAsync(ClientOwned(client), administrator));
    }

    [Fact]
    public async Task AClientUserOfTheOwningClientDoesNot()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var client = await this._harness.SeedClientAsync(tenant);
        var administrator = await this._harness.SeedAdministratorAsync(
            tenantId: tenant,
            tenantRole: TenantRole.TenantUser);
        await this._harness.SeedClientAssignmentAsync(client, administrator, ClientRole.ClientUser);

        Assert.False(await this._ownerRoles.HoldsOwnerRoleAsync(ClientOwned(client), administrator));
    }

    // A tenant administrator holds client administrator on every client of their tenant, which is how the
    // request path resolves the same caller's roles.
    [Fact]
    public async Task ATenantAdministratorOfTheOwningClientsTenantHoldsTheRoleWithNoAssignmentOfTheirOwn()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var client = await this._harness.SeedClientAsync(tenant);
        var administrator = await this._harness.SeedAdministratorAsync(tenantId: tenant);

        Assert.True(await this._ownerRoles.HoldsOwnerRoleAsync(ClientOwned(client), administrator));
    }

    // An assignment to a client inside a real tenant counts only while the administrator is still a member of
    // that tenant. The request path applies the same qualification, so admitting one here would let a
    // credential be stored for someone the route would refuse.
    [Fact]
    public async Task AnAssignmentSurvivingTheLossOfTenantMembershipDoesNotHoldTheRole()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var client = await this._harness.SeedClientAsync(tenant);
        var administrator = await this._harness.SeedAdministratorAsync(
            tenantId: tenant,
            tenantRole: TenantRole.TenantUser);
        await this._harness.SeedClientAssignmentAsync(client, administrator);
        await this._harness.RevokeTenantMembershipsAsync(administrator);

        Assert.False(await this._ownerRoles.HoldsOwnerRoleAsync(ClientOwned(client), administrator));
    }

    [Fact]
    public async Task ADisabledAccountHoldsNothing()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var administrator = await this._harness.SeedAdministratorAsync(
            isPlatformAdministrator: true,
            tenantId: tenant,
            isActive: false);

        Assert.False(await this._ownerRoles.HoldsOwnerRoleAsync(TenantOwned(tenant), administrator));
    }

    [Fact]
    public async Task AConnectionOwnedByNeitherIsHeldOnlyByAPlatformAdministrator()
    {
        var tenant = await this._harness.SeedTenantAsync();
        var platform = await this._harness.SeedAdministratorAsync(isPlatformAdministrator: true);
        var scoped = await this._harness.SeedAdministratorAsync(tenantId: tenant);

        Assert.True(await this._ownerRoles.HoldsOwnerRoleAsync(Unowned(), platform));
        Assert.False(await this._ownerRoles.HoldsOwnerRoleAsync(Unowned(), scoped));
    }

    [Fact]
    public async Task AnInvocationThatRecordedNoAdministratorHoldsNothing()
    {
        var tenant = await this._harness.SeedTenantAsync();

        Assert.False(await this._ownerRoles.HoldsOwnerRoleAsync(TenantOwned(tenant), adminId: null));
    }

    // The refusal names the role, so an operator reading an invocation that failed to store learns what has to
    // be restored rather than only that something was denied.
    [Theory]
    [InlineData(true, false, "client administrator")]
    [InlineData(false, true, "tenant administrator")]
    [InlineData(false, false, "platform administrator")]
    public void TheRequirementNamesTheRoleTheOwnerAsksFor(bool clientOwned, bool tenantOwned, string expected)
    {
        var connection = Connection(
            clientOwned ? Guid.NewGuid() : null,
            tenantOwned ? Guid.NewGuid() : null);

        Assert.Contains(expected, this._ownerRoles.DescribeRequirement(connection), StringComparison.Ordinal);
    }

    private static AiConnectionDto ClientOwned(Guid clientId)
    {
        return Connection(clientId, tenantId: null);
    }

    private static AiConnectionDto TenantOwned(Guid tenantId)
    {
        return Connection(clientId: null, tenantId);
    }

    private static AiConnectionDto Unowned()
    {
        return Connection(clientId: null, tenantId: null);
    }

    private static AiConnectionDto Connection(Guid? clientId, Guid? tenantId)
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            clientId,
            "Example connection",
            "meisterdev/openAiCompatible",
            "https://api.example.com/v1",
            "meisterdev/openAiCompatible:ApiKey",
            AiDiscoveryMode.ManualOnly,
            IsActive: true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TenantId: tenantId);
    }
}
