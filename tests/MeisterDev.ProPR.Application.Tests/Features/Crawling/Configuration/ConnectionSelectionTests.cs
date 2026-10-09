// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Configuration;

public sealed class ConnectionSelectionTests
{
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();

    [Fact]
    public async Task EmptySources_UseNativeCoordinateMappingIndependentlyOfLabels()
    {
        var connections = this.Connections();
        var native = Substitute.For<IRepositoryDiscoveryProvider>();
        native.Descriptor.Returns(
            new ConnectionDiscoveryDescriptor(
                ScmProvider.GitHub, "Synthetic boundary", null,
                [new(ProCursorSourceKind.Repository, "Synthetic source")], false, false));
        native.ListScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("native-key", "Synthetic label")]);
        native.GetConfigurationCoordinates(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<ConnectionDiscoveryScope>(), null)
            .Returns(new ConnectionDiscoveryCoordinates(null, "https://native.example.com/config", "native-coordinate"));
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(native);
        var service = new ReviewConfigurationSelectionService(registry, connections);

        var selected = await service.ResolveConnectionSelectionAsync(
            this._clientId, this._connectionId,
            "native-key", null, null, null, null);

        Assert.Equal("https://native.example.com/config", selected.ProviderScopePath);
        Assert.Equal("native-coordinate", selected.ProviderProjectKey);
        await native.DidNotReceiveWithAnyArgs().ListSourcesAsync(null!, null!, null, default, default);
    }

    [Fact]
    public async Task SelectedScope_MustBelongToTheSelectedConnectionBeforeNativeProjectRequests()
    {
        var scopeId = Guid.NewGuid();
        var native = Substitute.For<IRepositoryDiscoveryProvider>();
        native.Descriptor.Returns(
            new ConnectionDiscoveryDescriptor(
                ScmProvider.GitHub, "Scope", "Project",
                [new(ProCursorSourceKind.Repository, "Source")], false, false));
        native.ListScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("scope", "Scope", scopeId)]);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        var admin = Substitute.For<IProviderAdminDiscoveryService>();
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(native);
        registry.GetProviderAdminDiscoveryService(ScmProvider.GitHub).Returns(admin);
        var service = new ReviewConfigurationSelectionService(registry, this.Connections(), scopes);
        var context = await service.GetConnectionContextAsync(this._clientId, this._connectionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetProjectsAsync(context, "scope"));
        await admin.DidNotReceiveWithAnyArgs().ListProjectOptionsAsync(default, default, default, default);
        await scopes.Received().GetByIdAsync(this._clientId, this._connectionId, scopeId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuppliedProviderConflict_RefusesBeforeNativeRequests()
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        var service = new ReviewConfigurationSelectionService(registry, this.Connections());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveConnectionSelectionAsync(
            this._clientId, this._connectionId, "scope", null, ScmProvider.GitLab, null, null));
        registry.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Fact]
    public async Task EmptyFilters_DoNotAllowAnUnavailableProjectSelection()
    {
        var scopeId = Guid.NewGuid();
        var native = Substitute.For<IRepositoryDiscoveryProvider>();
        native.Descriptor.Returns(
            new ConnectionDiscoveryDescriptor(
                ScmProvider.GitHub, "Scope", "Project",
                [new(ProCursorSourceKind.Repository, "Source")], false, false));
        native.ListScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("scope", "Scope", scopeId)]);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByIdAsync(this._clientId, this._connectionId, scopeId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmScopeDto(
                    scopeId, this._clientId, this._connectionId, "organization", "native",
                    "https://native.example.com", "Scope", "verified", true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var admin = Substitute.For<IProviderAdminDiscoveryService>();
        admin.ListProjectOptionsAsync(this._clientId, scopeId, Arg.Any<CancellationToken>(), this._connectionId)
            .Returns<IReadOnlyList<ScmDiscoveryProjectOption>>([new(scopeId, "available", "Available")]);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(native);
        registry.GetProviderAdminDiscoveryService(ScmProvider.GitHub).Returns(admin);
        var service = new ReviewConfigurationSelectionService(registry, this.Connections(), scopes);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveConnectionSelectionAsync(
            this._clientId, this._connectionId, "scope", "missing", null, null, null));
        native.DidNotReceiveWithAnyArgs().GetConfigurationCoordinates(null!, null!, null);
        await native.DidNotReceiveWithAnyArgs().ListSourcesAsync(null!, null!, null, default, default);
    }

    [Fact]
    public async Task ConnectionOwnedByAnotherClient_IsRefusedBeforeNativeRequests()
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        var connections = this.Connections();
        connections.GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    this._connectionId, Guid.NewGuid(), ScmProvider.GitHub,
                    "https://github.com", ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        var service = new ReviewConfigurationSelectionService(registry, connections);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetConnectionContextAsync(this._clientId, this._connectionId));
        registry.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task SourceDiscovery_RefusesUnavailableScopeBeforeRepositoryQueries(ScmProvider provider)
    {
        var native = Substitute.For<IRepositoryDiscoveryProvider>();
        native.Descriptor.Returns(
            new ConnectionDiscoveryDescriptor(
                provider, "Owner", null,
                [new(ProCursorSourceKind.Repository, "Repository")], false, false));
        native.ListScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("allowed", "Allowed")]);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(provider).Returns(native);
        var service = new ReviewConfigurationSelectionService(registry, this.Connections());
        var context = new ConnectionDiscoveryContext(this._clientId, this._connectionId, new(provider, "https://native.example.com"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSourcesAsync(context, "outside", null, ProCursorSourceKind.Repository));
        await native.DidNotReceiveWithAnyArgs().ListSourcesAsync(null!, null!, null, default, default);
    }

    [Theory]
    [InlineData("Native Workspace", false)]
    [InlineData("ABCDEF01-0000-0000-0000-000000000001", false)]
    [InlineData("ABCDEF01-0000-0000-0000-000000000001", true)]
    public async Task SavedNativeProjectNameOrIdCase_ValidatesWithoutChangingStoredCoordinates(string savedProject, bool conflictingProjectName)
    {
        var scopeId = Guid.NewGuid();
        var nativeId = "abcdef01-0000-0000-0000-000000000001";
        var native = Substitute.For<IRepositoryDiscoveryProvider>();
        native.Descriptor.Returns(
            new ConnectionDiscoveryDescriptor(
                ScmProvider.GitHub, "Scope", "Workspace",
                [new(ProCursorSourceKind.Repository, "Repository")], false, false));
        native.ListScopesAsync(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ConnectionDiscoveryScope>>([new("scope", "Scope", scopeId)]);
        native.GetConfigurationCoordinates(Arg.Any<ConnectionDiscoveryContext>(), Arg.Any<ConnectionDiscoveryScope>(), savedProject)
            .Returns(new ConnectionDiscoveryCoordinates(scopeId, "https://native.example.com", savedProject));
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByIdAsync(this._clientId, this._connectionId, scopeId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmScopeDto(
                    scopeId, this._clientId, this._connectionId, "organization", "native", "https://native.example.com",
                    "Scope", "verified", true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var admin = Substitute.For<IProviderAdminDiscoveryService>();
        admin.ListProjectOptionsAsync(this._clientId, scopeId, Arg.Any<CancellationToken>(), this._connectionId)
            .Returns<IReadOnlyList<ScmDiscoveryProjectOption>>(
                conflictingProjectName
                    ? [new(scopeId, nativeId, "Native Workspace"), new(scopeId, "other-project", savedProject)]
                    : [new(scopeId, nativeId, "Native Workspace")]);
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(native);
        registry.GetProviderAdminDiscoveryService(ScmProvider.GitHub).Returns(admin);
        var service = new ReviewConfigurationSelectionService(registry, this.Connections(), scopes);

        var selected = await service.ResolveConnectionSelectionAsync(
            this._clientId, this._connectionId, "scope", savedProject, null, scopeId, "https://native.example.com");
        Assert.Equal(savedProject, selected.ProviderProjectKey);
        var context = await service.GetConnectionContextAsync(this._clientId, this._connectionId);
        await service.GetSourcesAsync(context, "scope", savedProject, ProCursorSourceKind.Repository);
        await native.Received(1).ListSourcesAsync(context, "scope", nativeId, ProCursorSourceKind.Repository, Arg.Any<CancellationToken>());
    }

    private IClientScmConnectionRepository Connections()
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    this._connectionId, this._clientId, ScmProvider.GitHub,
                    "https://github.com", ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true));
        return connections;
    }
}
