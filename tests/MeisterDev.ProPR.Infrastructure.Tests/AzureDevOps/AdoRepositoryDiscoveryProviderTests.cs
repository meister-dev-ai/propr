using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoRepositoryDiscoveryProviderTests
{
    [Fact]
    public async Task SelectedConnection_SourceDiscoveryUsesItsOwnedScopeAndCredentialIdentity()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var context = new ConnectionDiscoveryContext(clientId, connectionId, new(ScmProvider.AzureDevOps, "https://dev.azure.com"));
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    connectionId, clientId, ScmProvider.AzureDevOps, "https://dev.azure.com",
                    ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ClientScmScopeDto>>(
            [
                new(
                    scopeId, clientId, connectionId, "organization", "org", "https://dev.azure.com/org",
                    "Organization", "verified", true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        discovery.ListProjectOptionsAsync(clientId, scopeId, Arg.Any<CancellationToken>(), connectionId)
            .Returns<IReadOnlyList<ScmDiscoveryProjectOption>>([new(scopeId, "project", "Project")]);
        discovery.ListSourceOptionsAsync(clientId, scopeId, "project", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>(), connectionId)
            .Returns<IReadOnlyList<ScmDiscoverySourceOption>>([new("repository", new("azureDevOps", "native-repository"), "Repository", "main")]);
        var provider = new AdoRepositoryDiscoveryProvider(connections, scopes, discovery);

        var source = Assert.Single(await provider.ListSourcesAsync(context, "https://dev.azure.com/org", "project", ProCursorSourceKind.Repository));

        Assert.Equal(scopeId, source.OrganizationScopeId);
        Assert.Equal("native-repository", source.RepositoryId);
        await discovery.Received(1).ListSourceOptionsAsync(
            clientId, scopeId, "project", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>(), connectionId);
        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default, default);
    }

    [Fact]
    public async Task SelectedConnection_RejectsAnotherConnectionIdentityBeforeReadingScopes()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(), clientId, ScmProvider.AzureDevOps, "https://dev.azure.com",
                    ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        var scopes = Substitute.For<IClientScmScopeRepository>();
        var provider = new AdoRepositoryDiscoveryProvider(connections, scopes, Substitute.For<IProviderAdminDiscoveryService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ListScopesAsync(new(clientId, connectionId, new(ScmProvider.AzureDevOps, "https://dev.azure.com"))));
        await scopes.DidNotReceiveWithAnyArgs().GetByConnectionIdAsync(default, default, default);
    }

    [Fact]
    public async Task ListRepositoriesAsync_PreservesRepositoryDisplayName()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com");
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var scopes = Substitute.For<IClientScmScopeRepository>();
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        connections.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>(
                [
                    new ClientScmConnectionDto(
                        connectionId, clientId, ScmProvider.AzureDevOps,
                        "https://dev.azure.com", ScmAuthenticationKind.PersonalAccessToken,
                        "Azure DevOps", true, "verified", DateTimeOffset.UtcNow, null, null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ]));
        scopes.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ClientScmScopeDto>>(
                [
                    new ClientScmScopeDto(
                        scopeId, clientId, connectionId, "organization", "org",
                        "https://dev.azure.com/org", "org", "verified", true, DateTimeOffset.UtcNow,
                        null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ]));
        discovery.ListProjectOptionsAsync(clientId, scopeId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ScmDiscoveryProjectOption>>(
                [
                    new ScmDiscoveryProjectOption(scopeId, "project-id", "Project"),
                ]));
        discovery.ListSourceOptionsAsync(clientId, scopeId, "project-id", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ScmDiscoverySourceOption>>(
                [
                    new ScmDiscoverySourceOption(
                        "repository", new CanonicalSourceReferenceDto("AzureDevOps", "repo-guid"),
                        "my-repo", null),
                ]));

        var provider = new AdoRepositoryDiscoveryProvider(connections, scopes, discovery);
        var repository = Assert.Single(await provider.ListRepositoriesAsync(clientId, host, "https://dev.azure.com/org"));

        Assert.Equal("repo-guid", repository.ExternalRepositoryId);
        Assert.Equal("my-repo", repository.RepositoryName);
        Assert.Equal("project-id", repository.ProjectPath);
        Assert.Equal("Project", repository.ProjectDisplayName);
    }
}
