// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Clients;

/// <summary>
///     What a client's provider connection is allowed to list, and what naming the wrong connection gets.
/// </summary>
public sealed class ClientProviderDiscoveryControllerTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid ConnectionId = Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222");

    [Fact]
    public async Task GetScopes_ListsWhatTheConnectionCanReach()
    {
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery.ListScopesAsync(ClientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(["acme", "contoso"]);

        var controller = CreateController(discovery);

        var result = await controller.GetScopes(ClientId, ScmProvider.GitHub, ConnectionId);

        var scopes = Assert.IsAssignableFrom<IReadOnlyList<ProviderScopeOptionResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(["acme", "contoso"], scopes.Select(scope => scope.ScopePath));
    }

    [Fact]
    public async Task GetRepositories_ReportsTheProviderNativeIdentifierAndItsPath()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery.ListRepositoriesAsync(ClientId, Arg.Any<ProviderHostRef>(), "acme", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryRef>>([new RepositoryRef(host, "101", "acme", "acme/platform")]);

        var controller = CreateController(discovery);

        var result = await controller.GetRepositories(ClientId, ScmProvider.GitHub, ConnectionId, "acme");

        var repositories = Assert.IsAssignableFrom<IReadOnlyList<ProviderRepositoryOptionResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        var repository = Assert.Single(repositories);
        Assert.Equal("101", repository.RepositoryId);
        Assert.Equal("acme/platform", repository.DisplayName);
    }

    /// <summary>
    ///     Two providers can sit at one host, so the provider in the route has to agree with the connection's
    ///     own. Otherwise a client could list one provider's repositories through the other's adapter.
    /// </summary>
    [Fact]
    public async Task GetScopes_NamingAConnectionForAnotherProvider_IsRefused()
    {
        var controller = CreateController(Substitute.For<IRepositoryDiscoveryProvider>());

        var result = await controller.GetScopes(ClientId, ScmProvider.GitLab, ConnectionId);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetScopes_NamingADeactivatedConnection_IsRefused()
    {
        var controller = CreateController(Substitute.For<IRepositoryDiscoveryProvider>(), isActive: false);

        var result = await controller.GetScopes(ClientId, ScmProvider.GitHub, ConnectionId);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    ///     A token that cannot list is reported as a refusal. An empty list would read as an owner with no
    ///     repositories, which is a different thing and sends an operator looking in the wrong place.
    /// </summary>
    [Fact]
    public async Task GetRepositories_WhenTheProviderRefuses_SaysSoRatherThanAnsweringEmpty()
    {
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery.ListRepositoriesAsync(ClientId, Arg.Any<ProviderHostRef>(), "acme", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryRef>>(_ => throw new InvalidOperationException("GitHub repository discovery failed with status 403."));

        var controller = CreateController(discovery);

        var result = await controller.GetRepositories(ClientId, ScmProvider.GitHub, ConnectionId, "acme");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetRepositories_WithoutAScope_IsRefusedAsInvalid()
    {
        var controller = CreateController(Substitute.For<IRepositoryDiscoveryProvider>());

        var result = await controller.GetRepositories(ClientId, ScmProvider.GitHub, ConnectionId, "  ");

        Assert.IsType<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
    }

    [Fact]
    public async Task GetScopes_ForAProviderWithNoDiscovery_IsNotFound()
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>())
            .Returns(_ => throw new InvalidOperationException("No IRepositoryDiscoveryProvider is registered."));

        var controller = CreateController(Substitute.For<IRepositoryDiscoveryProvider>(), registry: registry);

        var result = await controller.GetScopes(ClientId, ScmProvider.GitHub, ConnectionId);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    ///     Discovery exists to build a mention configuration, so an installation not entitled to answer
    ///     mentions is not asked to enumerate what a client's token can reach. Unconditional here, where the
    ///     Azure DevOps discovery endpoints take a purpose and hold the capability only for one of them: those
    ///     are shared between callers, and a gate only some callers ask for is no gate.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discovery_WhenTheInstallationCannotAnswerMentions_AsksTheProviderNothing(bool scopes)
    {
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        var controller = CreateController(discovery, licensing: UnavailableMentionAnswering());

        var result = scopes
            ? await controller.GetScopes(ClientId, ScmProvider.GitHub, ConnectionId)
            : await controller.GetRepositories(ClientId, ScmProvider.GitHub, ConnectionId, "acme");

        Assert.IsType<PremiumFeatureUnavailableResult>(result);
        await discovery.DidNotReceiveWithAnyArgs().ListScopesAsync(default, null!, default);
        await discovery.DidNotReceiveWithAnyArgs().ListRepositoriesAsync(default, null!, null!, default);
    }

    private static ILicensingCapabilityService UnavailableMentionAnswering()
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetCapabilityAsync(PremiumCapabilityKey.MentionAnswering, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new CapabilitySnapshot(
                        PremiumCapabilityKey.MentionAnswering,
                        "Mention answering",
                        true,
                        true,
                        PremiumCapabilityOverrideState.Disabled,
                        false,
                        "Mention answering is currently disabled for this installation.")));

        return licensing;
    }

    /// <summary>
    ///     Only a client administrator may list what a connection can reach. Discovery exists to build a
    ///     mention configuration, which is an administrator action, and the tab that calls it is behind the
    ///     same role.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discovery_AsAClientUser_IsRefusedWithoutAskingTheProvider(bool scopes)
    {
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        var controller = CreateController(discovery, role: ClientRole.ClientUser);

        var result = scopes
            ? await controller.GetScopes(ClientId, ScmProvider.GitHub, ConnectionId)
            : await controller.GetRepositories(ClientId, ScmProvider.GitHub, ConnectionId, "acme");

        Assert.IsNotType<OkObjectResult>(result);
        await discovery.DidNotReceiveWithAnyArgs().ListScopesAsync(default, null!, default);
        await discovery.DidNotReceiveWithAnyArgs().ListRepositoriesAsync(default, null!, null!, default);
    }

    private static ClientProviderDiscoveryController CreateController(
        IRepositoryDiscoveryProvider discovery,
        bool isActive = true,
        IScmProviderRegistry? registry = null,
        ILicensingCapabilityService? licensing = null,
        ClientRole role = ClientRole.ClientAdministrator)
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(ClientId, ConnectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionDto(
                    ConnectionId,
                    ClientId,
                    ScmProvider.GitHub,
                    "https://github.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    isActive,
                    "verified",
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow));

        var providerRegistry = registry ?? Substitute.For<IScmProviderRegistry>();
        if (registry is null)
        {
            providerRegistry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>()).Returns(discovery);
        }

        var controller = new ClientProviderDiscoveryController(
            connections,
            providerRegistry,
            NullLogger<ClientProviderDiscoveryController>.Instance,
            licensing)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
        {
            [ClientId] = role,
        };

        return controller;
    }
}
