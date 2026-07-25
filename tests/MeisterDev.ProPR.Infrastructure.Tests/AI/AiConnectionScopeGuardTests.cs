// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Repositories;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests the rule that a logical-model mapping may only reference a connection profile owned by the same
///     tenant as the scope that references it, so one tenant's credentials can never be used by another.
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

    private AiConnectionScopeGuard Sut()
    {
        return new AiConnectionScopeGuard(this._clients);
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
