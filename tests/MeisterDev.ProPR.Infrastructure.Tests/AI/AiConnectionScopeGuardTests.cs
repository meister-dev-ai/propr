// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Repositories;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests the rules a reference to a connection profile has to satisfy: it may only cross into the tenant that
///     owns the profile, and it may only use a provider family that tenant permits. Both live here because both
///     are asked at the same two moments — when a reference is written and before a credential is used.
/// </summary>
public sealed class AiConnectionScopeGuardTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("22222222-bbbb-0000-0000-000000000002");
    private static readonly Guid ClientInA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ClientInB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly IClientRegistry _clients = Substitute.For<IClientRegistry>();

    public AiConnectionScopeGuardTests()
    {
        this._clients.GetTenantIdAsync(ClientInA, Arg.Any<CancellationToken>()).Returns(TenantA);
        this._clients.GetTenantIdAsync(ClientInB, Arg.Any<CancellationToken>()).Returns(TenantB);
    }

    private readonly ITenantProviderPolicyProvider _policies = Substitute.For<ITenantProviderPolicyProvider>();

    private AiConnectionScopeGuard Sut()
    {
        this._policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        return new AiConnectionScopeGuard(this._clients, this._policies);
    }

    [Fact]
    public async Task TenantScopedConnection_ReferencedByOwningTenant_IsPermitted()
    {
        var connection = Connection(tenantId: TenantA);

        Assert.Null(await this.Sut().ValidateAsync(connection, TenantA));
    }

    [Fact]
    public async Task TenantScopedConnection_ReferencedByAnotherTenant_IsRefused()
    {
        var connection = Connection(tenantId: TenantA);

        var reason = await this.Sut().ValidateAsync(connection, TenantB);

        Assert.NotNull(reason);
        Assert.Contains("tenant", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientScopedConnection_ReferencedFromSameTenant_IsPermitted()
    {
        // A connection owned by one client is still within its tenant, so a tenant-wide entry or a
        // sibling client in that same tenant may reference it. Only crossing a tenant boundary is refused.
        var connection = Connection(clientId: ClientInA);

        Assert.Null(await this.Sut().ValidateAsync(connection, TenantA));
    }

    [Fact]
    public async Task ClientScopedConnection_ReferencedFromAnotherTenant_IsRefused()
    {
        var connection = Connection(clientId: ClientInA);

        var reason = await this.Sut().ValidateAsync(connection, TenantB);

        Assert.NotNull(reason);
    }

    [Fact]
    public async Task ConnectionWithNoOwner_IsRefused()
    {
        // Neither tenant- nor client-scoped: the owning tenant cannot be established, so the reference
        // is refused rather than assumed safe.
        var connection = Connection();

        Assert.NotNull(await this.Sut().ValidateAsync(connection, TenantA));
    }

    [Fact]
    public async Task ClientScopedConnection_WhoseClientHasNoTenant_IsRefused()
    {
        var orphan = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");
        this._clients.GetTenantIdAsync(orphan, Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var connection = Connection(clientId: orphan);

        Assert.NotNull(await this.Sut().ValidateAsync(connection, TenantA));
    }

    // The runtime half of the allow-list: a profile inside the right tenant is still refused when its provider
    // family is not on that tenant's list, and the refusal explains itself rather than reading as a scope error.
    [Fact]
    public async Task ConnectionWhoseProviderTheTenantForbids_IsRefused()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([AiProviderKind.OpenAiCompatible]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies);

        var reason = await guard.ValidateAsync(connection, TenantA);

        Assert.NotNull(reason);
        Assert.Contains("AzureOpenAi", reason, StringComparison.Ordinal);
        Assert.Contains("permitted provider list", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionWhoseProviderTheTenantPermits_IsAllowed()
    {
        var connection = Connection(tenantId: TenantA);
        this._policies.GetForTenantAsync(TenantA, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([AiProviderKind.AzureOpenAi]));
        var guard = new AiConnectionScopeGuard(this._clients, this._policies);

        Assert.Null(await guard.ValidateAsync(connection, TenantA));
    }

    // A host composed without the policy provider keeps working: the tenant boundary is still enforced, and the
    // allow-list simply has nothing to say.
    [Fact]
    public async Task WithNoPolicyProvider_TheTenantBoundaryIsStillEnforced()
    {
        var guard = new AiConnectionScopeGuard(this._clients);

        Assert.Null(await guard.ValidateAsync(Connection(tenantId: TenantA), TenantA));
        Assert.NotNull(await guard.ValidateAsync(Connection(tenantId: TenantA), TenantB));
    }

    private static AiConnectionDto Connection(Guid? clientId = null, Guid? tenantId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiConnectionDto(
            Guid.NewGuid(),
            clientId,
            "Scoped Connection",
            AiProviderKind.AzureOpenAi,
            "https://test.openai.azure.com",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            now,
            now,
            Secret: "secret",
            TenantId: tenantId);
    }
}
