using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoRepositoryDiscoveryProviderTests
{
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
        discovery.ListProjectsAsync(clientId, scopeId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<AdoProjectOptionDto>>(
                [
                    new AdoProjectOptionDto(scopeId, "project-id", "Project"),
                ]));
        discovery.ListSourcesAsync(clientId, scopeId, "project-id", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>(
                [
                    new AdoSourceOptionDto(
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
