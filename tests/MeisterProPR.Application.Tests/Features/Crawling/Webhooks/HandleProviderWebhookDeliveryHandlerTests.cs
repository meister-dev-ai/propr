// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Crawling.Webhooks;

public sealed class HandleProviderWebhookDeliveryHandlerTests
{
    [Fact]
    public async Task HandleAsync_AzureDevOpsCommentDelivery_UsesWebhookSecretAndSharedSynchronization()
    {
        var configuration = CreateConfiguration([WebhookEventType.PullRequestCommented]);
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, configuration.OrganizationUrl);
        var repository = new RepositoryRef(host, "repo-1", "project", "project");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var reviewer = new ReviewerIdentity(host, "reviewer-guid", "meister-bot", "Meister Bot", true);
        var ingressService = Substitute.For<IWebhookIngressService>();
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();

        configurationRepository.GetActiveByPathKeyAsync("path-key", Arg.Any<CancellationToken>())
            .Returns(configuration);
        providerRegistry.GetWebhookIngressService(ScmProvider.AzureDevOps).Returns(ingressService);
        secretProtectionCodec.Unprotect(configuration.SecretCiphertext!, "WebhookSecret").Returns("webhook-secret");
        clientRegistry.GetReviewerIdentityAsync(configuration.ClientId, host, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewerIdentity?>(reviewer));
        ingressService.VerifyAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                "webhook-secret",
                Arg.Any<CancellationToken>())
            .Returns(true);
        ingressService.ParseAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new WebhookDeliveryEnvelope(
                    host,
                    "delivery-1",
                    "pull_request.commented",
                    "ms.vss-code.git-pullrequest-comment-event",
                    repository,
                    review,
                    null,
                    "refs/heads/feature/test",
                    "refs/heads/main",
                    null));
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.Submitted,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Processed shared Azure DevOps comment delivery."]));
        deliveryLogRepository.AddAsync(
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

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);
        var payload = "{\"eventType\":\"ms.vss-code.git-pullrequest-comment-event\"}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Basic valid",
        };

        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.AzureDevOps, "path-key", headers, payload),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Accepted, result.DeliveryOutcome);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal("accepted", result.ResponseStatus);
        Assert.Contains("Processed shared Azure DevOps comment delivery.", result.ActionSummaries);

        await ingressService.Received(1)
            .VerifyAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                payload,
                "webhook-secret",
                Arg.Any<CancellationToken>());
        await synchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.Provider == ScmProvider.AzureDevOps &&
                    request.Host == host &&
                    request.Repository == repository &&
                    request.CodeReview == review &&
                    request.RequestedReviewerIdentity == reviewer &&
                    request.SummaryLabel == "pull request commented" &&
                    request.PullRequestStatus == PrStatus.Active &&
                    request.AllowReviewSubmission),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DisabledProvider_ReturnsNotFoundWithoutResolvingConfiguration()
    {
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();
        var providerActivationService = Substitute.For<IProviderActivationService>();

        providerActivationService.IsEnabledAsync(ScmProvider.GitHub, Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService,
            providerActivationService);

        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.GitHub, "path-key", CreateHeaders(), "{}"),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Rejected, result.DeliveryOutcome);
        Assert.Equal(404, result.HttpStatusCode);

        await configurationRepository.DidNotReceive()
            .GetActiveByPathKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        providerRegistry.DidNotReceiveWithAnyArgs().GetWebhookIngressService(default);
        await synchronizationService.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_AzureDevOpsSparseRepositoryPayload_UsesCanonicalFilterRepositoryIdentity()
    {
        var configuration = new WebhookConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WebhookProviderType.AzureDevOps,
            "path-key",
            "https://dev.azure.com/org",
            "project-guid",
            true,
            DateTimeOffset.UtcNow,
            [WebhookEventType.PullRequestCommented],
            [
                new WebhookRepoFilterDto(
                    Guid.NewGuid(),
                    "meister-propr",
                    ["main"],
                    new CanonicalSourceReferenceDto("azureDevOps", "repo-guid"),
                    "Meister ProPR"),
            ],
            SecretCiphertext: "ciphertext");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, configuration.OrganizationUrl);
        var sparseRepository = new RepositoryRef(host, "meister-propr", "meister-propr", "meister-propr");
        var sparseReview = new CodeReviewRef(sparseRepository, CodeReviewPlatformKind.PullRequest, "26", 26);
        var ingressService = Substitute.For<IWebhookIngressService>();
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();

        configurationRepository.GetActiveByPathKeyAsync("path-key", Arg.Any<CancellationToken>())
            .Returns(configuration);
        providerRegistry.GetWebhookIngressService(ScmProvider.AzureDevOps).Returns(ingressService);
        secretProtectionCodec.Unprotect(configuration.SecretCiphertext!, "WebhookSecret").Returns("webhook-secret");
        ingressService.VerifyAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                "webhook-secret",
                Arg.Any<CancellationToken>())
            .Returns(true);
        ingressService.ParseAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new WebhookDeliveryEnvelope(
                    host,
                    "delivery-1",
                    "pull_request.commented",
                    "ms.vss-code.git-pullrequest-comment-event",
                    sparseRepository,
                    sparseReview,
                    null,
                    "refs/heads/feature/test",
                    "refs/heads/main",
                    null));
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.Submitted,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Submitted review intake job."]));
        deliveryLogRepository.AddAsync(
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

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);

        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.AzureDevOps, "path-key", CreateHeaders(), "{}"),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Accepted, result.DeliveryOutcome);

        await synchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.ProviderProjectKey == "project-guid"
                    && request.RepositoryId == "repo-guid"
                    && request.Repository != null
                    && request.Repository.ExternalRepositoryId == "repo-guid"
                    && request.Repository.OwnerOrNamespace == "project-guid"
                    && request.Repository.ProjectPath == "project-guid"
                    && request.CodeReview != null
                    && request.CodeReview.Repository.ExternalRepositoryId == "repo-guid"
                    && request.CodeReview.Repository.ProjectPath == "project-guid"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnknownPathKey_ReturnsGenericNotFoundWithoutPersistingLog()
    {
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();

        configurationRepository.GetActiveByPathKeyAsync("missing-path", Arg.Any<CancellationToken>())
            .Returns((WebhookConfigurationDto?)null);

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);

        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.GitHub, "missing-path", CreateHeaders(), "{}"),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Rejected, result.DeliveryOutcome);
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Null(result.FailureReason);

        await deliveryLogRepository.DidNotReceiveWithAnyArgs()
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
        providerRegistry.DidNotReceiveWithAnyArgs().GetWebhookIngressService(default);
        await synchronizationService.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_PathKeyForDifferentProvider_ReturnsGenericNotFoundAndPersistsGenericRejection()
    {
        var configuration = CreateConfiguration([WebhookEventType.PullRequestCommented]);
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();

        configurationRepository.GetActiveByPathKeyAsync("path-key", Arg.Any<CancellationToken>())
            .Returns(configuration);
        deliveryLogRepository.AddAsync(
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
            .ReturnsForAnyArgs(_ => Task.FromResult(CreateLogEntry(WebhookDeliveryOutcome.Rejected, 404)));

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);

        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.GitHub, "path-key", CreateHeaders(), "{}"),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Rejected, result.DeliveryOutcome);
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Null(result.FailureReason);

        await deliveryLogRepository.Received(1)
            .AddAsync(
                configuration.Id,
                Arg.Any<DateTimeOffset>(),
                "unknown",
                WebhookDeliveryOutcome.Rejected,
                404,
                null,
                null,
                null,
                null,
                Arg.Is<IReadOnlyList<string>>(summaries => summaries.Count == 0),
                null,
                Arg.Any<CancellationToken>());
        providerRegistry.DidNotReceiveWithAnyArgs().GetWebhookIngressService(default);
        await synchronizationService.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_InvalidWebhookSignature_ReturnsUnauthorizedWithoutParsingPayload()
    {
        var configuration = CreateConfiguration([WebhookEventType.PullRequestCommented]);
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, configuration.OrganizationUrl);
        var ingressService = Substitute.For<IWebhookIngressService>();
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();

        configurationRepository.GetActiveByPathKeyAsync("path-key", Arg.Any<CancellationToken>())
            .Returns(configuration);
        providerRegistry.GetWebhookIngressService(ScmProvider.AzureDevOps).Returns(ingressService);
        secretProtectionCodec.Unprotect(configuration.SecretCiphertext!, "WebhookSecret").Returns("webhook-secret");
        ingressService.VerifyAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                "webhook-secret",
                Arg.Any<CancellationToken>())
            .Returns(false);
        deliveryLogRepository.AddAsync(
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
            .ReturnsForAnyArgs(_ => Task.FromResult(CreateLogEntry(WebhookDeliveryOutcome.Rejected, 401)));

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);

        var payload = "{\"eventType\":\"ms.vss-code.git-pullrequest-comment-event\"}";
        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.AzureDevOps, "path-key", CreateHeaders(), payload),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Rejected, result.DeliveryOutcome);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.Equal("Webhook signature or authorization header was missing or invalid.", result.FailureReason);

        await ingressService.DidNotReceiveWithAnyArgs().ParseAsync(default, default!, default!, default!);
        await synchronizationService.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
        await deliveryLogRepository.Received(1)
            .AddAsync(
                configuration.Id,
                Arg.Any<DateTimeOffset>(),
                "unknown",
                WebhookDeliveryOutcome.Rejected,
                401,
                null,
                null,
                null,
                null,
                Arg.Is<IReadOnlyList<string>>(summaries => summaries.Count == 0),
                "Webhook signature or authorization header was missing or invalid.",
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task
        HandleAsync_ForgejoDelivery_WithSyntheticRevision_RefreshesLatestProviderRevisionBeforeSynchronization()
    {
        var configuration = new WebhookConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WebhookProviderType.Forgejo,
            "path-key",
            "https://codeberg.example.com",
            "local_admin",
            true,
            DateTimeOffset.UtcNow,
            [WebhookEventType.PullRequestUpdated],
            [new WebhookRepoFilterDto(Guid.NewGuid(), "local_admin/propr", ["main"], null, "propr")],
            SecretCiphertext: "ciphertext");
        var host = new ProviderHostRef(ScmProvider.Forgejo, configuration.OrganizationUrl);
        var repository = new RepositoryRef(host, "local_admin/propr", "local_admin", "local_admin/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "100001", 1);
        var invalidRevision = new ReviewRevision(
            "review_requested-head-sha",
            "base-sha",
            "base-sha",
            "review_requested-head-sha",
            "base-sha...review_requested-head-sha");
        var refreshedRevision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var ingressService = Substitute.For<IWebhookIngressService>();
        var queryService = Substitute.For<ICodeReviewQueryService>();
        var configurationRepository = Substitute.For<IWebhookConfigurationRepository>();
        var deliveryLogRepository = Substitute.For<IWebhookDeliveryLogRepository>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
        var synchronizationService = Substitute.For<IPullRequestSynchronizationService>();

        configurationRepository.GetActiveByPathKeyAsync("path-key", Arg.Any<CancellationToken>())
            .Returns(configuration);
        providerRegistry.GetWebhookIngressService(ScmProvider.Forgejo).Returns(ingressService);
        providerRegistry.GetCodeReviewQueryService(ScmProvider.Forgejo).Returns(queryService);
        secretProtectionCodec.Unprotect(configuration.SecretCiphertext!, "WebhookSecret").Returns("webhook-secret");
        clientRegistry.GetReviewerIdentityAsync(configuration.ClientId, host, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewerIdentity?>(reviewer));
        ingressService.VerifyAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                "webhook-secret",
                Arg.Any<CancellationToken>())
            .Returns(true);
        ingressService.ParseAsync(
                configuration.ClientId,
                host,
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new WebhookDeliveryEnvelope(
                    host,
                    "delivery-1",
                    "reviewer_assignment",
                    "pull_request",
                    repository,
                    review,
                    invalidRevision,
                    "feature/test-branch",
                    "main",
                    null));
        queryService.GetLatestRevisionAsync(configuration.ClientId, review, Arg.Any<CancellationToken>())
            .Returns(refreshedRevision);
        synchronizationService.SynchronizeAsync(
                Arg.Any<PullRequestSynchronizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.Submitted,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Submitted review intake job."]));
        deliveryLogRepository.AddAsync(
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

        var sut = new HandleProviderWebhookDeliveryHandler(
            configurationRepository,
            deliveryLogRepository,
            providerRegistry,
            clientRegistry,
            secretProtectionCodec,
            NullLogger<HandleProviderWebhookDeliveryHandler>.Instance,
            synchronizationService);

        var result = await sut.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(ScmProvider.Forgejo, "path-key", CreateHeaders(), "{}"),
            CancellationToken.None);

        Assert.Equal(WebhookDeliveryOutcome.Accepted, result.DeliveryOutcome);

        await queryService.Received(1)
            .GetLatestRevisionAsync(configuration.ClientId, review, Arg.Any<CancellationToken>());
        await synchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.Provider == ScmProvider.Forgejo
                    && request.CodeReview == review
                    && request.ReviewRevision == refreshedRevision),
                Arg.Any<CancellationToken>());
    }

    private static Dictionary<string, string> CreateHeaders()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Basic valid",
        };
    }

    private static WebhookConfigurationDto CreateConfiguration(IReadOnlyList<WebhookEventType> enabledEvents)
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
            enabledEvents,
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
            "ms.vss-code.git-pullrequest-comment-event",
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
