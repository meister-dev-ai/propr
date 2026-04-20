// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Controllers;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Crawling.Webhooks;

public sealed class ProviderWebhookReceiverControllerTests
{
    [Theory]
    [InlineData("ado")]
    [InlineData("azureDevOps")]
    public async Task Receive_AzureDevOpsAliases_ReturnAcceptedPayloadFromSharedHandler(string provider)
    {
        var configRepo = Substitute.For<IWebhookConfigurationRepository>();
        var logRepo = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var ingressService = Substitute.For<IWebhookIngressService>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();
        var configuration = CreateConfiguration();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, configuration.OrganizationUrl);
        var reviewer = new ReviewerIdentity(host, "reviewer-guid", "meister-bot", "Meister Bot", true);
        var delivery = CreateEnvelope(host, "pull_request.updated", "git.pullrequest.updated");

        configRepo.GetActiveByPathKeyAsync("path-key", Arg.Any<CancellationToken>()).Returns(configuration);
        providerRegistry.GetWebhookIngressService(ScmProvider.AzureDevOps).Returns(ingressService);
        secretProtectionCodec.Unprotect(configuration.SecretCiphertext!, "WebhookSecret").Returns("webhook-secret");
        clientRegistry.GetReviewerIdentityAsync(configuration.ClientId, host, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewerIdentity?>(reviewer));
        ingressService.VerifyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(true);
        ingressService.ParseAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(delivery);
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.Submitted,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Submitted shared review intake job."]));
        logRepo.AddAsync(
                default,
                default,
                default!,
                default,
                default,
                default,
                default,
                default,
                default,
                default!,
                default)
            .ReturnsForAnyArgs(_ => Task.FromResult(CreateLogEntry(WebhookDeliveryOutcome.Accepted, 200)));

        var providerHandler = CreateProviderHandler(
            configRepo,
            logRepo,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            synchronizationService);
        var controller = CreateController(providerHandler, "Basic valid");

        using var payload = AdoWebhookPayloadFactory.PullRequestUpdated();
        var result = await controller.Receive(
            provider,
            "path-key",
            payload.RootElement.Clone(),
            CancellationToken.None);

        var ok = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        var response = Assert.IsType<WebhookDeliveryResponse>(ok.Value);
        Assert.Equal("accepted", response.Status);
        await logRepo.Received(1)
            .AddAsync(
                configuration.Id,
                Arg.Any<DateTimeOffset>(),
                "git.pullrequest.updated",
                WebhookDeliveryOutcome.Accepted,
                StatusCodes.Status200OK,
                "repo-1",
                42,
                "refs/heads/feature/test",
                "refs/heads/main",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Receive_UnsupportedProvider_ReturnsNotFoundWithoutRoutingToSharedHandler()
    {
        var configRepo = Substitute.For<IWebhookConfigurationRepository>();
        var logRepo = Substitute.For<IWebhookDeliveryLogRepository>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var providerHandler = CreateProviderHandler(
            configRepo,
            logRepo,
            Substitute.For<IScmProviderRegistry>(),
            Substitute.For<IClientRegistry>(),
            secretProtectionCodec,
            Substitute.For<IPullRequestSynchronizationService>());
        var controller = CreateController(providerHandler, "Basic valid");

        using var payload = AdoWebhookPayloadFactory.PullRequestCreated();
        var result = await controller.Receive("svn", "path-key", payload.RootElement.Clone(), CancellationToken.None);

        var status = Assert.IsType<NotFoundResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, status.StatusCode);
        await logRepo.DidNotReceiveWithAnyArgs()
            .AddAsync(
                default,
                default,
                default!,
                default,
                default,
                default,
                default,
                default,
                default,
                default!,
                default);
    }

    private static ProviderWebhookReceiverController CreateController(
        HandleProviderWebhookDeliveryHandler providerHandler,
        string authorizationHeader)
    {
        var controller = new ProviderWebhookReceiverController(
            providerHandler,
            NullLogger<ProviderWebhookReceiverController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        controller.HttpContext.Request.Headers.Authorization = authorizationHeader;
        controller.HttpContext.Request.Method = HttpMethods.Post;
        return controller;
    }

    private static HandleProviderWebhookDeliveryHandler CreateProviderHandler(
        IWebhookConfigurationRepository configRepo,
        IWebhookDeliveryLogRepository logRepo,
        IScmProviderRegistry providerRegistry,
        IClientRegistry clientRegistry,
        ISecretProtectionCodec secretProtectionCodec,
        IPullRequestSynchronizationService synchronizationService)
    {
        return new HandleProviderWebhookDeliveryHandler(
            configRepo,
            logRepo,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);
    }

    private static WebhookDeliveryEnvelope CreateEnvelope(ProviderHostRef host, string deliveryKind, string eventName)
    {
        var repository = new RepositoryRef(host, "repo-1", "project", "project");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);

        return new WebhookDeliveryEnvelope(
            host,
            "delivery-1",
            deliveryKind,
            eventName,
            repository,
            review,
            null,
            "refs/heads/feature/test",
            "refs/heads/main",
            null);
    }

    private static WebhookConfigurationDto CreateConfiguration()
    {
        return new WebhookConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WebhookProviderType.AzureDevOps,
            "path-key",
            "https://dev.azure.com/org",
            "project",
            true,
            DateTimeOffset.UtcNow,
            [WebhookEventType.PullRequestCreated, WebhookEventType.PullRequestUpdated],
            [
                new WebhookRepoFilterDto(
                    Guid.NewGuid(),
                    "repo-1",
                    ["main"],
                    new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                    "repo-1"),
            ],
            SecretCiphertext: "ciphertext");
    }

    private static WebhookDeliveryLogEntryDto CreateLogEntry(WebhookDeliveryOutcome outcome, int statusCode)
    {
        return new WebhookDeliveryLogEntryDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "git.pullrequest.updated",
            outcome,
            statusCode,
            "repo-1",
            42,
            "refs/heads/feature/test",
            "refs/heads/main",
            [],
            null);
    }
}
