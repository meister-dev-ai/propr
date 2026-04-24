// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoWebhookIngressServiceTests
{
    [Fact]
    public async Task ProviderAdapters_RegisterWebhookIngressUnderNeutralInterface()
    {
        var verifier = Substitute.For<IAdoWebhookBasicAuthVerifier>();
        var parser = Substitute.For<IAdoWebhookPayloadParser>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");

        verifier.IsAuthorized("Basic valid", "ciphertext").Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton(verifier);
        services.AddSingleton(parser);
        services.AddSingleton(clientRegistry);
        services.AddAzureDevOpsProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ingressService = scope.ServiceProvider
            .GetServices<IWebhookIngressService>()
            .Single(service => service.Provider == ScmProvider.AzureDevOps);

        Assert.IsType<AdoWebhookIngressService>(ingressService);

        var verified = await ingressService.VerifyAsync(
            Guid.NewGuid(),
            host,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Basic valid",
            },
            "{}",
            "ciphertext",
            CancellationToken.None);

        Assert.True(verified);
        verifier.Received(1).IsAuthorized("Basic valid", "ciphertext");
    }

    [Fact]
    public async Task ParseAsync_UsesProviderLocalPayloadParserThroughNeutralIngressInterface()
    {
        var clientId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var verifier = Substitute.For<IAdoWebhookBasicAuthVerifier>();
        var parser = Substitute.For<IAdoWebhookPayloadParser>();
        var clientRegistry = Substitute.For<IClientRegistry>();

        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, reviewerId.ToString("D"), "review-bot", "Review Bot", true));
        parser.Parse(Arg.Any<string>(), Arg.Any<JsonElement>())
            .Returns(
                new IncomingAdoWebhookDelivery(
                    "providers/ado",
                    "git.pullrequest.updated",
                    WebhookEventType.PullRequestUpdated,
                    "repo-1",
                    42,
                    "refs/heads/feature/providers",
                    "refs/heads/main",
                    "active",
                    [reviewerId]));

        var services = new ServiceCollection();
        services.AddSingleton(verifier);
        services.AddSingleton(parser);
        services.AddSingleton(clientRegistry);
        services.AddAzureDevOpsProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ingressService = scope.ServiceProvider
            .GetServices<IWebhookIngressService>()
            .Single(service => service.Provider == ScmProvider.AzureDevOps);

        var envelope = await ingressService.ParseAsync(
            clientId,
            host,
            new Dictionary<string, string>(),
            "{}",
            CancellationToken.None);

        Assert.Equal("reviewer_assignment", envelope.DeliveryKind);
        Assert.Equal("repo-1", envelope.Repository!.ExternalRepositoryId);
        Assert.Equal(42, envelope.Review!.Number);
        parser.Received(1).Parse("providers/ado", Arg.Any<JsonElement>());
    }
}
