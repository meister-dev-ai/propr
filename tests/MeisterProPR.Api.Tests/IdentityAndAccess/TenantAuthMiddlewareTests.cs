// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Middleware;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantAuthMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WithValidatedJwt_PopulatesTenantRolesInHttpContext()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var jwtTokenService = Substitute.For<IJwtTokenService>();
        jwtTokenService.ValidateAccessToken("valid-token")
            .Returns(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[]
                        {
                            new Claim("sub", userId.ToString()),
                            new Claim("global_role", AppUserRole.User.ToString()),
                        },
                        "Bearer")));

        var userRepository = Substitute.For<IUserRepository>();
        var user = new AppUser
        {
            Id = userId,
            Username = "tenant.admin",
            GlobalRole = AppUserRole.User,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.TenantMemberships.Add(
            new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Role = TenantRole.TenantAdministrator,
                AssignedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        user.ClientAssignments.Add(
            new UserClientRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = clientId,
                Role = ClientRole.ClientAdministrator,
                AssignedAt = DateTimeOffset.UtcNow,
            });
        userRepository.GetByIdWithAssignmentsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(user);

        var services = new ServiceCollection()
            .AddSingleton(jwtTokenService)
            .AddSingleton(userRepository)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = "Bearer valid-token";

        var middleware = new AuthMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var clientRoles = Assert.IsType<Dictionary<Guid, ClientRole>>(context.Items["ClientRoles"]);
        Assert.Equal(ClientRole.ClientAdministrator, clientRoles[clientId]);
        var tenantRoles = Assert.IsType<Dictionary<Guid, TenantRole>>(context.Items["TenantRoles"]);
        Assert.Equal(TenantRole.TenantAdministrator, tenantRoles[tenantId]);
        await userRepository.DidNotReceive().GetUserClientRolesAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WithPat_PopulatesRoleDictionariesFromLoadedUserAndUsesRequestCancellation()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var patRepository = Substitute.For<IUserPatRepository>();
        var userRepository = Substitute.For<IUserRepository>();
        var user = new AppUser
        {
            Id = userId,
            Username = "tenant.user",
            GlobalRole = AppUserRole.User,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.ClientAssignments.Add(
            new UserClientRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = clientId,
                Role = ClientRole.ClientUser,
                AssignedAt = DateTimeOffset.UtcNow,
            });
        user.TenantMemberships.Add(
            new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Role = TenantRole.TenantUser,
                AssignedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        patRepository.GetActiveByRawTokenAsync("valid-pat", Arg.Any<CancellationToken>())
            .Returns(
                new UserPat
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TokenHash = "hash",
                    Label = "CLI",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
        userRepository.GetByIdWithAssignmentsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(user);

        var services = new ServiceCollection()
            .AddSingleton(patRepository)
            .AddSingleton(userRepository)
            .BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers["X-User-Pat"] = "valid-pat";
        context.RequestAborted = cts.Token;

        var middleware = new AuthMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var clientRoles = Assert.IsType<Dictionary<Guid, ClientRole>>(context.Items["ClientRoles"]);
        Assert.Equal(ClientRole.ClientUser, clientRoles[clientId]);
        var tenantRoles = Assert.IsType<Dictionary<Guid, TenantRole>>(context.Items["TenantRoles"]);
        Assert.Equal(TenantRole.TenantUser, tenantRoles[tenantId]);
        await patRepository.Received(1).GetActiveByRawTokenAsync("valid-pat", cts.Token);
        await userRepository.Received(1).GetByIdWithAssignmentsAsync(userId, cts.Token);
        await userRepository.DidNotReceive().GetUserClientRolesAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RequireTenantRole_UnauthenticatedCaller_ReturnsUnauthorized()
    {
        var result = AuthHelpers.RequireTenantRole(new DefaultHttpContext(), Guid.NewGuid(), TenantRole.TenantUser);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }

    [Fact]
    public void RequireTenantRole_WithMatchingTenantAdministrator_ReturnsNull()
    {
        var tenantId = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();
        context.Items["TenantRoles"] = new Dictionary<Guid, TenantRole>
        {
            [tenantId] = TenantRole.TenantAdministrator,
        };

        var result = AuthHelpers.RequireTenantRole(context, tenantId, TenantRole.TenantAdministrator);

        Assert.Null(result);
    }

    [Fact]
    public void RequireTenantRole_WithMissingTenantMembership_ReturnsForbidden()
    {
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();
        context.Items["TenantRoles"] = new Dictionary<Guid, TenantRole>();

        var result = AuthHelpers.RequireTenantRole(context, Guid.NewGuid(), TenantRole.TenantUser);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }
}
