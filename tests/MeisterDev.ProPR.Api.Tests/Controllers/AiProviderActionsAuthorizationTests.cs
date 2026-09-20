// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Who may act on a connection's declared actions, which is decided by the connection's owner and not by the
///     address the request arrived at.
/// </summary>
/// <remarks>
///     One rule serves all three of starting an action, submitting the values it asked for, and reading where a
///     run stands, because all three act on the same connection. It is applied before the family is reached, so a
///     refusal never runs any of the family's code.
/// </remarks>
public sealed class AiProviderActionsAuthorizationTests
{
    private static readonly Guid OwningClient = Guid.NewGuid();
    private static readonly Guid OwningTenant = Guid.NewGuid();

    [Fact]
    public void AClientAdministratorOfTheOwningClientMayActOnAClientOwnedConnection()
    {
        var context = Caller(clientRoles: new() { [OwningClient] = ClientRole.ClientAdministrator });

        Assert.Null(AiProviderActionsController.AuthorizeConnectionAccess(ClientOwned(), context));
    }

    [Fact]
    public void AClientUserOfTheOwningClientMayNot()
    {
        var context = Caller(clientRoles: new() { [OwningClient] = ClientRole.ClientUser });

        Assert.IsAssignableFrom<ObjectResult>(AiProviderActionsController.AuthorizeConnectionAccess(ClientOwned(), context));
    }

    // The role map is scoped, so a role on some other client is a role on some other client.
    [Fact]
    public void AnAdministratorOfAnotherClientMayNot()
    {
        var context = Caller(clientRoles: new() { [Guid.NewGuid()] = ClientRole.ClientAdministrator });

        Assert.IsAssignableFrom<ObjectResult>(AiProviderActionsController.AuthorizeConnectionAccess(ClientOwned(), context));
    }

    [Fact]
    public void ATenantAdministratorOfTheOwningTenantMayActOnATenantOwnedConnection()
    {
        var context = Caller(tenantRoles: new() { [OwningTenant] = TenantRole.TenantAdministrator });

        Assert.Null(AiProviderActionsController.AuthorizeConnectionAccess(TenantOwned(), context));
    }

    [Fact]
    public void ATenantMemberWhoIsNotAnAdministratorMayNot()
    {
        var context = Caller(tenantRoles: new() { [OwningTenant] = TenantRole.TenantUser });

        Assert.IsAssignableFrom<ObjectResult>(AiProviderActionsController.AuthorizeConnectionAccess(TenantOwned(), context));
    }

    // A connection in another tenant is refused by the same rule: the caller holds no role against that tenant.
    [Fact]
    public void AnAdministratorOfAnotherTenantMayNot()
    {
        var context = Caller(tenantRoles: new() { [Guid.NewGuid()] = TenantRole.TenantAdministrator });

        Assert.IsAssignableFrom<ObjectResult>(AiProviderActionsController.AuthorizeConnectionAccess(TenantOwned(), context));
    }

    // Owned by neither, so no scoped role can stand for it.
    [Fact]
    public void AConnectionWithNeitherOwnerIsReachableOnlyByAPlatformAdministrator()
    {
        Assert.Null(AiProviderActionsController.AuthorizeConnectionAccess(Unowned(), Caller(isAdmin: true)));

        Assert.IsAssignableFrom<ObjectResult>(
            AiProviderActionsController.AuthorizeConnectionAccess(
                Unowned(),
                Caller(clientRoles: new() { [OwningClient] = ClientRole.ClientAdministrator })));

        Assert.IsAssignableFrom<ObjectResult>(
            AiProviderActionsController.AuthorizeConnectionAccess(
                Unowned(),
                Caller(tenantRoles: new() { [OwningTenant] = TenantRole.TenantAdministrator })));
    }

    [Fact]
    public void APlatformAdministratorMayActOnEitherOwnershipShape()
    {
        Assert.Null(AiProviderActionsController.AuthorizeConnectionAccess(ClientOwned(), Caller(isAdmin: true)));
        Assert.Null(AiProviderActionsController.AuthorizeConnectionAccess(TenantOwned(), Caller(isAdmin: true)));
    }

    [Fact]
    public void ACallerWithNoIdentityIsRefusedBeforeAnyRoleIsRead()
    {
        var refusal = AiProviderActionsController.AuthorizeConnectionAccess(ClientOwned(), new DefaultHttpContext());

        var result = Assert.IsAssignableFrom<ObjectResult>(refusal);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    private static AiConnectionDto ClientOwned()
    {
        return Connection(OwningClient, tenantId: null);
    }

    private static AiConnectionDto TenantOwned()
    {
        return Connection(clientId: null, OwningTenant);
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

    private static DefaultHttpContext Caller(
        bool isAdmin = false,
        Dictionary<Guid, ClientRole>? clientRoles = null,
        Dictionary<Guid, TenantRole>? tenantRoles = null)
    {
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();
        context.Items["IsAdmin"] = isAdmin;
        context.Items["ClientRoles"] = clientRoles ?? [];
        context.Items["TenantRoles"] = tenantRoles ?? [];

        return context;
    }
}
