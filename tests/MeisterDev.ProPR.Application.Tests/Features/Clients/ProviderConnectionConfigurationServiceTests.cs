// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Clients.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Clients;

public sealed class ProviderConnectionConfigurationServiceTests
{
    [Fact]
    public void ValidationUsesNativePolicyWithoutActivationOrCredentials()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var policy = Substitute.For<IScmConnectionConfigurationPolicy>();
        registry.GetConnectionConfigurationPolicy(ScmProvider.Forgejo).Returns(policy);
        var state = new ScmAuthenticationConfiguration(
            ScmProvider.Forgejo, "http://forgejo.example", ScmAuthenticationKind.PersonalAccessToken, null, null, null);
        policy.Validate(state).Returns([("AuthenticationKind", "native refusal")]);
        var errors = new ProviderConnectionConfigurationService(registry, Substitute.For<IClientScmScopeRepository>()).Validate(state);
        Assert.Equal("native refusal", Assert.Single(errors).Message);
        registry.DidNotReceive().IsRegistered(Arg.Any<ScmProvider>());
    }

    [Fact]
    public async Task HostVerificationUsesDiscoveryWithoutReadingSavedScopes()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var scopes = Substitute.For<IClientScmScopeRepository>();
        var policy = Substitute.For<IReviewSourcePolicy>();
        registry.GetReviewSourcePolicy(ScmProvider.GitHub).Returns(policy);
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        registry.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        var connection = Connection(ScmAuthenticationKind.PersonalAccessToken);
        await new ProviderConnectionConfigurationService(registry, scopes).VerifyAsync(connection);
        await discovery.Received(1).ListScopesAsync(
            connection.ClientId, new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl), Arg.Any<CancellationToken>());
        await scopes.DidNotReceiveWithAnyArgs().GetByConnectionIdAsync(default, default);
        registry.Received(1).GetReviewerIdentityService(connection.ProviderFamily);
    }

    [Fact]
    public async Task OrganizationVerificationRequiresEnabledScopeBeforeProviderRead()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var scopes = Substitute.For<IClientScmScopeRepository>();
        var policy = Substitute.For<IReviewSourcePolicy>();
        policy.RequiresOrganizationScope.Returns(true);
        policy.MissingVerificationScopeMessage.Returns("Add an enabled organization scope before verifying Azure DevOps provider connections.");
        registry.GetReviewSourcePolicy(ScmProvider.GitHub).Returns(policy);
        var connection = Connection(ScmAuthenticationKind.PersonalAccessToken);
        scopes.GetByConnectionIdAsync(connection.ClientId, connection.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<ClientScmScopeDto>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProviderConnectionConfigurationService(registry, scopes).VerifyAsync(connection));
        Assert.Equal("Add an enabled organization scope before verifying Azure DevOps provider connections.", error.Message);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
    }

    [Theory]
    [InlineData(ScmAuthenticationKind.AppInstallation, false, true)]
    [InlineData(ScmAuthenticationKind.PersonalAccessToken, false, false)]
    [InlineData(ScmAuthenticationKind.PersonalAccessToken, true, true)]
    public void PatchPreservesCompatibleSecretStateAndPersistedAppFields(ScmAuthenticationKind kind, bool replacement, bool compatible)
    {
        var existing = Connection(ScmAuthenticationKind.AppInstallation) with { AppId = 12, InstallationId = 34 };
        var result = ProviderConnectionConfigurationService.ResolvePatchAuthentication(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.GetConnectionConfigurationPolicy(existing.ProviderFamily), kind, null, null, null, null,
            null, replacement, existing);
        Assert.Equal(compatible, result.HasCompatibleSecretMaterial);
        Assert.Equal(kind == ScmAuthenticationKind.AppInstallation ? 12L : null, result.PersistedAppId);
        Assert.Equal(kind == ScmAuthenticationKind.AppInstallation ? 34L : null, result.PersistedInstallationId);
    }

    private static ClientScmConnectionDto Connection(ScmAuthenticationKind kind) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.GitHub, "https://github.example", kind, "Connection", true, "verified", null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
