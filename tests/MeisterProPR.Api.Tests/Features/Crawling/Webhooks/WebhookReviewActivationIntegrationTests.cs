// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Features.Crawling.Execution.Models;
using MeisterProPR.Application.Features.Crawling.Execution.Ports;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Crawling.Webhooks;

public sealed class WebhookReviewActivationIntegrationTests(WebhookReviewActivationIntegrationTests.WebhookReceiverApiFactory factory)
    : IClassFixture<WebhookReviewActivationIntegrationTests.WebhookReceiverApiFactory>
{
    [Fact]
    public async Task Receive_ActivationDelivery_PersistsAcceptedActionSummary()
    {
        await factory.ResetDeliveryLogsAsync();
        factory.ConfigureActivationScenario();
        var client = factory.CreateClient();

        using var request = factory.CreateRequest(AdoWebhookPayloadFactory.PullRequestUpdated());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.GetLatestDeliveryAsync();
        Assert.NotNull(persisted);
        Assert.Equal(WebhookDeliveryOutcome.Accepted, persisted!.DeliveryOutcome);
        Assert.Contains(
            persisted.ActionSummaries,
            summary => summary.Contains("Submitted review intake job", StringComparison.OrdinalIgnoreCase));
        await factory.SynchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.ActivationSource == PullRequestActivationSource.Webhook &&
                    request.SummaryLabel == "pull request updated" &&
                    request.ClientId == factory.ClientId &&
                    request.ProviderScopePath == "https://dev.azure.com/org" &&
                    request.ProviderProjectKey == "project" &&
                    request.RepositoryId == "repo-1" &&
                    request.PullRequestId == 42 &&
                    request.PullRequestStatus == PrStatus.Active),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Receive_ClosedDelivery_PersistsLifecycleActionSummary()
    {
        await factory.ResetDeliveryLogsAsync();
        factory.ConfigureLifecycleScenario();
        var client = factory.CreateClient();

        using var request = factory.CreateRequest(AdoWebhookPayloadFactory.PullRequestAbandoned());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await factory.GetLatestDeliveryAsync();
        Assert.NotNull(persisted);
        Assert.Equal(WebhookDeliveryOutcome.Accepted, persisted!.DeliveryOutcome);
        Assert.Contains(
            persisted.ActionSummaries,
            summary => summary.Contains("Cancelled 1 active review job", StringComparison.OrdinalIgnoreCase));
        await factory.SynchronizationService.Received(1)
            .SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.ActivationSource == PullRequestActivationSource.Webhook &&
                    request.SummaryLabel == "pull request closed" &&
                    request.PullRequestStatus == PrStatus.Abandoned &&
                    request.RepositoryId == "repo-1" &&
                    request.PullRequestId == 42),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Receive_LegacyAdoRoute_ReturnsNotFound()
    {
        await factory.ResetDeliveryLogsAsync();
        factory.ConfigureActivationScenario();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/v1/ado/path-key")
        {
            Content = new StringContent(
                AdoWebhookPayloadFactory.PullRequestUpdated().RootElement.GetRawText(),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("ado-webhook:secret")));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await factory.GetLatestDeliveryAsync());
    }

    public sealed class WebhookReceiverApiFactory : WebApplicationFactory<Program>
    {
        private readonly IWebhookReviewActivationService _activationService =
            Substitute.For<IWebhookReviewActivationService>();

        private readonly IAdoWebhookBasicAuthVerifier _authVerifier = Substitute.For<IAdoWebhookBasicAuthVerifier>();
        private readonly IClientRegistry _clientRegistry = Substitute.For<IClientRegistry>();
        private readonly string _dbName = $"TestDb_WebhookReceiver_{Guid.NewGuid():N}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();
        private readonly IWebhookIngressService _ingressService = Substitute.For<IWebhookIngressService>();

        private readonly IWebhookReviewLifecycleSyncService _lifecycleSyncService =
            Substitute.For<IWebhookReviewLifecycleSyncService>();

        private readonly IAdoWebhookPayloadParser _payloadParser = Substitute.For<IAdoWebhookPayloadParser>();
        private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();

        public IPullRequestSynchronizationService SynchronizationService { get; } =
            Substitute.For<IPullRequestSynchronizationService>();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid WebhookConfigurationId { get; } = Guid.NewGuid();

        public void ConfigureActivationScenario()
        {
            this._ingressService.VerifyAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProviderHostRef>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(true);
            this._ingressService.ParseAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProviderHostRef>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(CreateEnvelope("pull_request.updated", "git.pullrequest.updated"));
            this.SynchronizationService.SynchronizeAsync(
                    Arg.Any<PullRequestSynchronizationRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    new PullRequestSynchronizationOutcome(
                        PullRequestSynchronizationReviewDecision.Submitted,
                        PullRequestSynchronizationLifecycleDecision.None,
                        ["Submitted review intake job for PR #42 at iteration 7 via pull request updated."]));
        }

        public void ConfigureLifecycleScenario()
        {
            this._ingressService.VerifyAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProviderHostRef>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(true);
            this._ingressService.ParseAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProviderHostRef>(),
                    Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .ReturnsForAnyArgs(CreateEnvelope("pull_request.closed", "git.pullrequest.updated"));
            this.SynchronizationService.SynchronizeAsync(
                    Arg.Any<PullRequestSynchronizationRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    new PullRequestSynchronizationOutcome(
                        PullRequestSynchronizationReviewDecision.None,
                        PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
                        ["Cancelled 1 active review job(s) for PR #42 because the pull request is abandoned."]));
        }

        public HttpRequestMessage CreateRequest(JsonDocument payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/v1/providers/ado/path-key")
            {
                Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("ado-webhook:secret")));
            return request;
        }

        public async Task ResetDeliveryLogsAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.WebhookDeliveryLogEntries.RemoveRange(db.WebhookDeliveryLogEntries);
            await db.SaveChangesAsync();
        }

        public async Task<WebhookDeliveryLogEntryRecord?> GetLatestDeliveryAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            return await db.WebhookDeliveryLogEntries
                .OrderByDescending(entry => entry.ReceivedAt)
                .FirstOrDefaultAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("ADO_SKIP_TOKEN_VALIDATION", "true");
            builder.UseSetting("ADO_STUB_PR", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-webhook-receiver-jwt-secret-32!");

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IWebhookConfigurationRepository, EfWebhookConfigurationRepository>();
                services.AddScoped<IWebhookDeliveryLogRepository, EfWebhookDeliveryLogRepository>();

                ReplaceService(services, this._authVerifier);
                ReplaceService(services, this._payloadParser);
                ReplaceService(services, this._activationService);
                ReplaceService(services, this._lifecycleSyncService);
                ReplaceService(services, this._clientRegistry);
                ReplaceService(services, this._providerRegistry);
                ReplaceService(services, this.SynchronizationService);

                services.AddSingleton(Substitute.For<IUserRepository>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                this._authVerifier.IsAuthorized(Arg.Any<string?>(), Arg.Any<string>()).Returns(true);
                this._payloadParser.Parse(Arg.Any<string>(), Arg.Any<JsonElement>())
                    .Returns(
                        new IncomingAdoWebhookDelivery(
                            "path-key",
                            "git.pullrequest.updated",
                            WebhookEventType.PullRequestUpdated,
                            "repo-1",
                            42,
                            "refs/heads/feature/test",
                            "refs/heads/main",
                            "active",
                            []));
                this._clientRegistry.GetReviewerIdentityAsync(
                        Arg.Any<Guid>(),
                        Arg.Any<ProviderHostRef>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ReviewerIdentity?>(null));
                this._providerRegistry.GetWebhookIngressService(ScmProvider.AzureDevOps)
                    .Returns(this._ingressService);
                this.SynchronizationService.SynchronizeAsync(
                        Arg.Any<PullRequestSynchronizationRequest>(),
                        Arg.Any<CancellationToken>())
                    .Returns(
                        new PullRequestSynchronizationOutcome(
                            PullRequestSynchronizationReviewDecision.Submitted,
                            PullRequestSynchronizationLifecycleDecision.None,
                            ["Submitted review intake job for PR #42 at iteration 7 via pull request updated."]));
            });
        }

        private static WebhookDeliveryEnvelope CreateEnvelope(string deliveryKind, string eventName)
        {
            var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org");
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

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "Webhook Receiver Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.WebhookConfigurations.Add(
                new WebhookConfigurationRecord
                {
                    Id = this.WebhookConfigurationId,
                    ClientId = this.ClientId,
                    ProviderType = WebhookProviderType.AzureDevOps,
                    PublicPathKey = "path-key",
                    OrganizationUrl = "https://dev.azure.com/org",
                    ProjectId = "project",
                    SecretCiphertext = "ciphertext",
                    IsActive = true,
                    EnabledEvents = [WebhookEventType.PullRequestUpdated.ToString()],
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }

        private static void ReplaceService<T>(IServiceCollection services, T implementation)
            where T : class
        {
            services.RemoveAll<T>();
            services.AddSingleton(implementation);
        }
    }
}
