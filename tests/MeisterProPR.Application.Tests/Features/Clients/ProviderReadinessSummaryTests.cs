// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Models;
using MeisterProPR.Application.Features.Clients.Services;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Clients;

public sealed class ProviderReadinessSummaryTests
{
    [Fact]
    public async Task GetForClientAsync_UsesLeastReadyConnectionForProviderFamilySummary()
    {
        var clientId = Guid.NewGuid();
        var hostedConnectionId = Guid.NewGuid();
        var selfHostedConnectionId = Guid.NewGuid();

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>(
                [
                    new ClientScmConnectionDto(
                        hostedConnectionId,
                        clientId,
                        ScmProvider.GitHub,
                        "https://github.com",
                        ScmAuthenticationKind.PersonalAccessToken,
                        null,
                        null,
                        "GitHub Cloud",
                        true,
                        "verified",
                        DateTimeOffset.UtcNow,
                        null,
                        null,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow),
                    new ClientScmConnectionDto(
                        selfHostedConnectionId,
                        clientId,
                        ScmProvider.GitHub,
                        "https://github.enterprise.example.com",
                        ScmAuthenticationKind.PersonalAccessToken,
                        null,
                        null,
                        "GitHub Enterprise",
                        true,
                        "verified",
                        DateTimeOffset.UtcNow,
                        null,
                        null,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow),
                ]));

        var readinessEvaluator = Substitute.For<IProviderReadinessEvaluator>();
        readinessEvaluator.EvaluateAsync(
                clientId,
                Arg.Is<ClientScmConnectionDto>(connection => connection.Id == hostedConnectionId),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new ProviderConnectionReadinessResult(
                        ProviderConnectionReadinessLevel.WorkflowComplete,
                        "hosted",
                        "Connection meets onboarding and workflow-complete readiness criteria.",
                        [],
                        [new ProviderReadinessCriterionResult("workflow", "connection", "satisfied", "ready")])));
        readinessEvaluator.EvaluateAsync(
                clientId,
                Arg.Is<ClientScmConnectionDto>(connection => connection.Id == selfHostedConnectionId),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new ProviderConnectionReadinessResult(
                        ProviderConnectionReadinessLevel.OnboardingReady,
                        "selfHosted",
                        "Connection is verified for onboarding, but workflow-complete readiness criteria are still missing.",
                        ["Self-hosted GitHub remains onboarding-ready."],
                        [
                            new ProviderReadinessCriterionResult(
                                "workflow",
                                "connection",
                                "unsatisfied",
                                "Self-hosted GitHub remains onboarding-ready."),
                        ])));

        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        providerRegistry.IsRegistered(ScmProvider.GitHub).Returns(true);

        var sut = new ProviderOperationalStatusService(connectionRepository, readinessEvaluator, providerRegistry);

        var result = await sut.GetForClientAsync(clientId, CancellationToken.None);

        Assert.Single(result.ProviderFamilies);
        Assert.Equal(ProviderConnectionReadinessLevel.OnboardingReady, result.ProviderFamilies[0].LeastReadyLevel);
        Assert.Equal(1, result.ProviderFamilies[0].WorkflowCompleteCount);
        Assert.Equal(1, result.ProviderFamilies[0].OnboardingReadyCount);
    }
}
