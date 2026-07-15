// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class CallerIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithValidatedJwt_PopulatesTenantRolesInHttpContext()
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

        var resolution = await CallerIdentityResolver.ResolveAsync(context, UserAuthenticationDefaults.Scheme);

        Assert.NotNull(resolution.Principal);
        var clientRoles = Assert.IsType<Dictionary<Guid, ClientRole>>(context.Items["ClientRoles"]);
        Assert.Equal(ClientRole.ClientAdministrator, clientRoles[clientId]);
        var tenantRoles = Assert.IsType<Dictionary<Guid, TenantRole>>(context.Items["TenantRoles"]);
        Assert.Equal(TenantRole.TenantAdministrator, tenantRoles[tenantId]);
        await userRepository.DidNotReceive().GetUserClientRolesAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_WithPat_PopulatesRoleDictionariesFromLoadedUserAndUsesRequestCancellation()
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

        var resolution = await CallerIdentityResolver.ResolveAsync(context, UserAuthenticationDefaults.Scheme);

        Assert.NotNull(resolution.Principal);
        var clientRoles = Assert.IsType<Dictionary<Guid, ClientRole>>(context.Items["ClientRoles"]);
        Assert.Equal(ClientRole.ClientUser, clientRoles[clientId]);
        var tenantRoles = Assert.IsType<Dictionary<Guid, TenantRole>>(context.Items["TenantRoles"]);
        Assert.Equal(TenantRole.TenantUser, tenantRoles[tenantId]);
        await patRepository.Received(1).GetActiveByRawTokenAsync("valid-pat", cts.Token);
        await userRepository.Received(1).GetByIdWithAssignmentsAsync(userId, cts.Token);
        await userRepository.DidNotReceive().GetUserClientRolesAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_WithCommunityEditionAndHiddenTenant_DoesNotDeriveClientRoles()
    {
        var userId = Guid.NewGuid();
        var hiddenTenantId = Guid.NewGuid();
        var hiddenClientId = Guid.NewGuid();
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
            Username = "community.user",
            GlobalRole = AppUserRole.User,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.TenantMemberships.Add(
            new TenantMembership
            {
                Id = Guid.NewGuid(),
                TenantId = hiddenTenantId,
                UserId = userId,
                Role = TenantRole.TenantAdministrator,
                AssignedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        userRepository.GetByIdWithAssignmentsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(user);

        var licensingCapabilityService = Substitute.For<ILicensingCapabilityService>();
        licensingCapabilityService.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(new LicensingSummaryDto(InstallationEdition.Community, DateTimeOffset.UtcNow, []));

        var services = new ServiceCollection();
        services.AddSingleton(jwtTokenService);
        services.AddSingleton(userRepository);
        services.AddSingleton(licensingCapabilityService);
        services.AddDbContext<MeisterProPRDbContext>(options =>
            options.UseInMemoryDatabase($"TenantAuthMiddlewareTests_{Guid.NewGuid()}"));

        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            dbContext.Clients.Add(
                new ClientRecord
                {
                    Id = hiddenClientId,
                    TenantId = hiddenTenantId,
                    DisplayName = "Hidden client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await dbContext.SaveChangesAsync();
        }

        await using var requestScope = provider.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer valid-token";

        var resolution = await CallerIdentityResolver.ResolveAsync(context, UserAuthenticationDefaults.Scheme);

        Assert.NotNull(resolution.Principal);
        var clientRoles = Assert.IsType<Dictionary<Guid, ClientRole>>(context.Items["ClientRoles"]);
        Assert.Empty(clientRoles);
        var tenantRoles = Assert.IsType<Dictionary<Guid, TenantRole>>(context.Items["TenantRoles"]);
        Assert.Equal(TenantRole.TenantAdministrator, tenantRoles[hiddenTenantId]);
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

    [Fact]
    public async Task ResolveAsync_RegularMemberOfRealTenantWithoutAssignment_DerivesNoClientRole()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUserWithTenantMembership(tenantId, TenantRole.TenantUser);

        var clientRoles = await ResolveEffectiveClientRolesAsync(user, tenantId, Guid.NewGuid());

        Assert.Empty(clientRoles);
    }

    [Fact]
    public async Task ResolveAsync_TenantAdministratorOfRealTenant_DerivesBlanketClientAdministrator()
    {
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var user = CreateUserWithTenantMembership(tenantId, TenantRole.TenantAdministrator);

        var clientRoles = await ResolveEffectiveClientRolesAsync(user, tenantId, clientId);

        Assert.Equal(ClientRole.ClientAdministrator, clientRoles[clientId]);
    }

    [Fact]
    public async Task ResolveAsync_RegularMemberOfSystemTenant_StillDerivesBlanketClientUser()
    {
        var clientId = Guid.NewGuid();
        var user = CreateUserWithTenantMembership(TenantCatalog.SystemTenantId, TenantRole.TenantUser);

        var clientRoles = await ResolveEffectiveClientRolesAsync(user, TenantCatalog.SystemTenantId, clientId);

        Assert.Equal(ClientRole.ClientUser, clientRoles[clientId]);
    }

    private static AppUser CreateUserWithTenantMembership(Guid tenantId, TenantRole role)
    {
        var userId = Guid.NewGuid();
        var user = new AppUser
        {
            Id = userId,
            Username = "tenant.member",
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
                Role = role,
                AssignedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        return user;
    }

    private static async Task<Dictionary<Guid, ClientRole>> ResolveEffectiveClientRolesAsync(
        AppUser user,
        Guid clientTenantId,
        Guid clientId)
    {
        var jwtTokenService = Substitute.For<IJwtTokenService>();
        jwtTokenService.ValidateAccessToken("valid-token")
            .Returns(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[]
                        {
                            new Claim("sub", user.Id.ToString()),
                            new Claim("global_role", AppUserRole.User.ToString()),
                        },
                        "Bearer")));

        var userRepository = Substitute.For<IUserRepository>();
        userRepository.GetByIdWithAssignmentsAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        // No licensing service registered => the resolver treats the installation as Commercial, so the
        // Community client-visibility filter does not run.
        var services = new ServiceCollection();
        services.AddSingleton(jwtTokenService);
        services.AddSingleton(userRepository);
        services.AddDbContext<MeisterProPRDbContext>(options =>
            options.UseInMemoryDatabase($"CallerIdentityResolverTests_{Guid.NewGuid()}"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        dbContext.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = clientTenantId,
                DisplayName = "Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await dbContext.SaveChangesAsync();

        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer valid-token";

        await CallerIdentityResolver.ResolveAsync(context, UserAuthenticationDefaults.Scheme);

        return Assert.IsType<Dictionary<Guid, ClientRole>>(context.Items["ClientRoles"]);
    }
}
