// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Clients;

public sealed class ClientProviderDiscoveryControllerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ConnectionId = Guid.NewGuid();

    [Fact]
    public async Task GetScopes_WhenOperationalActivationRefusesConnection_DoesNotDiscover()
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var registry = Substitute.For<IScmProviderRegistry>();
        var service = new ReviewConfigurationSelectionService(registry, connections);
        var controller = CreateController(service);

        Assert.IsType<BadRequestObjectResult>(await controller.GetScopes(ClientId, ConnectionId, "mention"));
        registry.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Fact]
    public async Task GetScopes_RefusesAnotherClientsOperationalConnectionBeforeNativeDiscovery()
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(ClientId, ConnectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    ConnectionId, Guid.NewGuid(), ScmProvider.GitHub,
                    "https://github.com", ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        var registry = Substitute.For<IScmProviderRegistry>();
        var controller = CreateController(new ReviewConfigurationSelectionService(registry, connections));

        Assert.IsType<BadRequestObjectResult>(await controller.GetScopes(ClientId, ConnectionId, "webhook"));
        registry.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other")]
    public async Task Discovery_RequiresAnExplicitSupportedPurpose(string? purpose)
    {
        var service = Substitute.For<IReviewConfigurationSelectionService>();
        var controller = CreateController(service);

        Assert.IsType<BadRequestObjectResult>(await controller.GetScopes(ClientId, ConnectionId, purpose));
        await service.DidNotReceiveWithAnyArgs().GetConnectionContextAsync(default, default, default);
    }

    [Theory]
    [InlineData("crawl", true)]
    [InlineData("webhook", true)]
    [InlineData("procursor", true)]
    [InlineData("mention", false)]
    public async Task Discovery_EnforcesPurposeRoleBeforeResolvingConnection(string purpose, bool allowed)
    {
        var service = ReadyService();
        var controller = CreateController(service, ClientRole.ClientUser);
        var result = await controller.GetScopes(ClientId, ConnectionId, purpose);

        if (allowed)
        {
            Assert.IsType<OkObjectResult>(result);
        }
        else
        {
            Assert.IsNotType<OkObjectResult>(result);
            await service.DidNotReceiveWithAnyArgs().GetConnectionContextAsync(default, default, default);
        }
    }

    [Theory]
    [InlineData("crawl", PremiumCapabilityKey.CrawlConfigs)]
    [InlineData("mention", PremiumCapabilityKey.MentionAnswering)]
    public async Task Discovery_EnforcesPurposeLicenseBeforeResolvingConnection(string purpose, string capabilityKey)
    {
        var service = ReadyService();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetCapabilityAsync(capabilityKey, Arg.Any<CancellationToken>())
            .Returns(new CapabilitySnapshot(capabilityKey, "Capability", true, PremiumCapabilityOverrideState.Disabled, false, "Disabled."));
        var controller = CreateController(service, licensing: licensing);

        Assert.IsType<PremiumFeatureUnavailableResult>(await controller.GetScopes(ClientId, ConnectionId, purpose));
        await service.DidNotReceiveWithAnyArgs().GetConnectionContextAsync(default, default, default);
    }

    [Fact]
    public async Task GetScopes_PassesTheSelectedConnectionContextAndReturnsNativeScopeIdentities()
    {
        var service = ReadyService();
        var context = await service.GetConnectionContextAsync(ClientId, ConnectionId);
        var savedScopeId = Guid.NewGuid();
        service.GetScopesAsync(context, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("native-scope", "Native label", savedScopeId)]);
        var controller = CreateController(service);

        var scopes = Assert.IsAssignableFrom<IReadOnlyList<ConnectionDiscoveryScope>>(
            Assert.IsType<OkObjectResult>(await controller.GetScopes(ClientId, ConnectionId, "webhook")).Value);
        Assert.Equal(savedScopeId, Assert.Single(scopes).SavedScopeId);
        await service.Received().GetScopesAsync(Arg.Is<ConnectionDiscoveryContext>(value => value.ConnectionId == ConnectionId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetProjects_DistinguishesUnsupportedFromAnEmptyListing()
    {
        var service = ReadyService();
        service.GetProjectsAsync(Arg.Any<ConnectionDiscoveryContext>(), "scope", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ScmDiscoveryProjectOption>>(_ => throw new NotSupportedException());
        var controller = CreateController(service);
        Assert.Equal(501, Assert.IsType<ObjectResult>(await controller.GetProjects(ClientId, ConnectionId, "webhook", "scope")).StatusCode);
    }

    [Fact]
    public async Task GetScopes_DistinguishesFailureFromAnEmptyListing()
    {
        var service = ReadyService();
        service.GetScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>(_ => throw new HttpRequestException());
        Assert.IsType<BadRequestObjectResult>(await CreateController(service).GetScopes(ClientId, ConnectionId, "webhook"));
    }

    [Fact]
    public async Task GetProjects_RefusesAnotherConnectionsScopeBeforeNativeProjectDiscovery()
    {
        var scopeId = Guid.NewGuid();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(ClientId, ConnectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    ConnectionId, ClientId, ScmProvider.AzureDevOps,
                    "https://dev.azure.com", ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        var native = Substitute.For<IRepositoryDiscoveryProvider>();
        native.Descriptor.Returns(new ConnectionDiscoveryDescriptor(ScmProvider.AzureDevOps, "Organization", "Project", [], true, true));
        native.ListScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("scope", "Organization", scopeId)]);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByIdAsync(ClientId, ConnectionId, scopeId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmScopeDto(
                    scopeId, ClientId, Guid.NewGuid(), "organization", "org", "https://dev.azure.com/org",
                    "Organization", "verified", true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var admin = Substitute.For<IProviderAdminDiscoveryService>();
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(ScmProvider.AzureDevOps).Returns(native);
        registry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps).Returns(admin);
        var controller = CreateController(new ReviewConfigurationSelectionService(registry, connections, scopes));

        Assert.IsType<BadRequestObjectResult>(await controller.GetProjects(ClientId, ConnectionId, "webhook", "scope"));
        await admin.DidNotReceiveWithAnyArgs().ListProjectOptionsAsync(default, default, default, default);
    }

    private static IReviewConfigurationSelectionService ReadyService()
    {
        var service = Substitute.For<IReviewConfigurationSelectionService>();
        service.GetConnectionContextAsync(ClientId, ConnectionId, Arg.Any<CancellationToken>())
            .Returns(new ConnectionDiscoveryContext(ClientId, ConnectionId, new(ScmProvider.GitHub, "https://github.com")));
        service.GetScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([]);
        service.SupportsMentionConfiguration(Arg.Any<ConnectionDiscoveryContext>()).Returns(true);
        return service;
    }

    private static ClientProviderDiscoveryController CreateController(
        IReviewConfigurationSelectionService service, ClientRole role = ClientRole.ClientAdministrator,
        ILicensingCapabilityService? licensing = null)
    {
        var controller = new ClientProviderDiscoveryController(service, licensing)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [ClientId] = role };
        return controller;
    }
}
