// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Services;
using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Clients;

public sealed class ProviderReadinessEvaluatorTests
{
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();

    private readonly IClientReviewerIdentityRepository _reviewerIdentityRepository =
        Substitute.For<IClientReviewerIdentityRepository>();

    private readonly IClientScmScopeRepository _scopeRepository = Substitute.For<IClientScmScopeRepository>();

    [Fact]
    public async Task EvaluateAsync_VerifiedConnectionWithoutReviewerIdentity_ReturnsOnboardingReady()
    {
        var connection = this.CreateConnection("https://github.com", "verified", DateTimeOffset.UtcNow);
        this._providerRegistry.IsRegistered(ScmProvider.GitHub).Returns(true);
        this._scopeRepository.GetByConnectionIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ClientScmScopeDto>>(
                [
                    new ClientScmScopeDto(
                        Guid.NewGuid(),
                        this._clientId,
                        this._connectionId,
                        "organization",
                        "acme",
                        "acme",
                        "Acme",
                        "verified",
                        true,
                        DateTimeOffset.UtcNow,
                        null,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow),
                ]));
        this._reviewerIdentityRepository.GetByConnectionIdAsync(
                this._clientId,
                this._connectionId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientReviewerIdentityDto?>(null));

        var sut = new ProviderReadinessEvaluator(
            this._scopeRepository,
            this._reviewerIdentityRepository,
            this._providerRegistry,
            new StaticProviderReadinessProfileCatalog());

        var result = await sut.EvaluateAsync(this._clientId, connection, CancellationToken.None);

        Assert.Equal(ProviderConnectionReadinessLevel.OnboardingReady, result.ReadinessLevel);
        Assert.Contains(
            result.MissingCriteria,
            criterion => criterion.Contains("reviewer identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateAsync_VerifiedHostedGitHubConnectionWithWorkflowInputs_ReturnsWorkflowComplete()
    {
        var connection = this.CreateConnection("https://github.com", "verified", DateTimeOffset.UtcNow);
        this._providerRegistry.IsRegistered(ScmProvider.GitHub).Returns(true);
        this._scopeRepository.GetByConnectionIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ClientScmScopeDto>>(
                [
                    new ClientScmScopeDto(
                        Guid.NewGuid(),
                        this._clientId,
                        this._connectionId,
                        "organization",
                        "acme",
                        "acme",
                        "Acme",
                        "verified",
                        true,
                        DateTimeOffset.UtcNow,
                        null,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow),
                ]));
        this._reviewerIdentityRepository.GetByConnectionIdAsync(
                this._clientId,
                this._connectionId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ClientReviewerIdentityDto?>(
                    new ClientReviewerIdentityDto(
                        Guid.NewGuid(),
                        this._clientId,
                        this._connectionId,
                        ScmProvider.GitHub,
                        "bot-1",
                        "meister-bot",
                        "Meister Bot",
                        true,
                        DateTimeOffset.UtcNow)));

        var sut = new ProviderReadinessEvaluator(
            this._scopeRepository,
            this._reviewerIdentityRepository,
            this._providerRegistry,
            new StaticProviderReadinessProfileCatalog());

        var result = await sut.EvaluateAsync(this._clientId, connection, CancellationToken.None);

        Assert.Equal(ProviderConnectionReadinessLevel.WorkflowComplete, result.ReadinessLevel);
        Assert.Empty(result.MissingCriteria);
    }

    [Fact]
    public async Task EvaluateAsync_StaleConnectionAfterVerification_ReturnsDegraded()
    {
        var connection = this.CreateConnection("https://gitlab.com", "stale", DateTimeOffset.UtcNow);
        this._providerRegistry.IsRegistered(ScmProvider.GitHub).Returns(true);
        this._scopeRepository.GetByConnectionIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmScopeDto>>([]));
        this._reviewerIdentityRepository.GetByConnectionIdAsync(
                this._clientId,
                this._connectionId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientReviewerIdentityDto?>(null));

        var sut = new ProviderReadinessEvaluator(
            this._scopeRepository,
            this._reviewerIdentityRepository,
            this._providerRegistry,
            new StaticProviderReadinessProfileCatalog());

        var result = await sut.EvaluateAsync(this._clientId, connection, CancellationToken.None);

        Assert.Equal(ProviderConnectionReadinessLevel.Degraded, result.ReadinessLevel);
    }

    private ClientScmConnectionDto CreateConnection(
        string hostBaseUrl,
        string verificationStatus,
        DateTimeOffset? lastVerifiedAt)
    {
        return new ClientScmConnectionDto(
            this._connectionId,
            this._clientId,
            ScmProvider.GitHub,
            hostBaseUrl,
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "Provider Connection",
            true,
            verificationStatus,
            lastVerifiedAt,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
