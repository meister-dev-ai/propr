// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Crawling.Webhooks.Validators;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Api.Tests.Validators;

public sealed class UnknownProviderConfigurationCompatibilityTests
{
    public static IEnumerable<object[]> SelectionCases()
    {
        foreach (var webhook in new[] { false, true })
        {
            foreach (var savedScope in new[] { false, true })
            {
                foreach (var state in new[] { "null", "empty", "populated" })
                {
                    yield return new object[] { webhook, savedScope, state };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public async Task CreateUnknownProviderWithPathRetainsAcceptedCoordinatesAndFilters(bool webhook, bool savedScope, string state)
    {
        var harness = new Harness(savedScope);
        var result = webhook
            ? await harness.Webhook().CreateWebhookConfiguration(harness.WebhookRequest(state), new CreateAdminWebhookConfigRequestValidator(harness.Selection))
            : await harness.Crawl().CreateCrawlConfiguration(harness.CrawlRequest(state), new CreateAdminCrawlConfigRequestValidator(harness.Selection));

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Null(harness.CreatedScopeId);
        Assert.Equal("https://provider.example/team", harness.CreatedPath);
        if (webhook)
        {
            var response = Assert.IsType<WebhookConfigurationResponse>(created.Value);
            Assert.Equal((WebhookProviderType)999, response.Provider);
            Assert.Contains("/providers/999/", response.ListenerUrl);
            Assert.Equal("one-time-test-secret", response.GeneratedSecret);
        }
        else
        {
            Assert.Equal((ScmProvider)999, Assert.IsType<CrawlConfigResponse>(created.Value).Provider);
        }

        harness.AssertFilters(state, creating: true);
        harness.AssertNoProviderCalls();
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public async Task PatchUnknownProviderRetainsNullEmptyAndPopulatedReplacementSemantics(bool webhook, bool savedScope, string state)
    {
        var harness = new Harness(savedScope);
        var result = webhook
            ? await harness.Webhook().PatchWebhookConfiguration(
                harness.Id,
                new PatchAdminWebhookConfigRequest { RepoFilters = harness.WebhookRequest(state).RepoFilters },
                new PatchAdminWebhookConfigRequestValidator())
            : await harness.Crawl().PatchCrawlConfiguration(
                harness.Id,
                new PatchAdminCrawlConfigRequest { RepoFilters = harness.CrawlRequest(state).RepoFilters },
                new PatchAdminCrawlConfigRequestValidator());

        Assert.IsType<OkObjectResult>(result);
        harness.AssertFilters(state, creating: false);
        harness.AssertNoProviderCalls();
    }

    [Fact]
    public async Task SuppliedWebhookActivationStillUsesStrictMappingBeforeSelection()
    {
        var harness = new Harness(false);
        var activation = Substitute.For<IProviderActivationService>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            harness.Webhook(activation).CreateWebhookConfiguration(
                harness.WebhookRequest("null"), new CreateAdminWebhookConfigRequestValidator(harness.Selection)));

        await activation.DidNotReceiveWithAnyArgs().IsEnabledAsync(default);
        harness.AssertNoProviderCalls();
    }

    [Fact]
    public async Task SuppliedCrawlActivationStillReceivesUnknownFamilyAndMayDisableIt()
    {
        var harness = new Harness(false);
        var activation = Substitute.For<IProviderActivationService>();
        activation.IsEnabledAsync((ScmProvider)999, Arg.Any<CancellationToken>()).Returns(false);

        var result = await harness.Crawl(activation).CreateCrawlConfiguration(
            harness.CrawlRequest("null"), new CreateAdminCrawlConfigRequestValidator(harness.Selection));

        Assert.IsType<ConflictObjectResult>(result);
        await activation.Received(1).IsEnabledAsync((ScmProvider)999, Arg.Any<CancellationToken>());
        Assert.Null(harness.CreatedPath);
        harness.AssertNoProviderCalls();
    }

    private sealed class Harness
    {
        private readonly ICrawlConfigurationRepository _crawl = Substitute.For<ICrawlConfigurationRepository>();
        private readonly IWebhookConfigurationRepository _webhooks = Substitute.For<IWebhookConfigurationRepository>();
        private readonly IClientAdminService _clients = Substitute.For<IClientAdminService>();
        private IReadOnlyList<CrawlRepoFilterDto>? _filters;
        private bool _replaced;

        public Harness(bool savedScope)
        {
            this.ScopeId = savedScope ? Guid.NewGuid() : null;
            this.Registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(_ => throw new InvalidOperationException("Unknown-family lookup"));
            this.Selection = new ReviewConfigurationSelectionService(this.Registry);
            this._clients.ExistsAsync(this.ClientId, Arg.Any<CancellationToken>()).Returns(true);
            this._crawl.GetByIdAsync(this.Id, Arg.Any<CancellationToken>()).Returns(_ => this.CrawlDto());
            this._webhooks.GetByIdAsync(this.Id, Arg.Any<CancellationToken>()).Returns(_ => this.WebhookDto());
            this._crawl.AddAsync(
                this.ClientId, (ScmProvider)999, Arg.Any<string>(), "project", 60, Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(), null).Returns(call =>
            {
                this.CreatedPath = call.ArgAt<string>(2);
                this.CreatedScopeId = call.ArgAt<Guid?>(5);
                return this.CrawlDto();
            });
            this._webhooks.AddAsync(
                    this.ClientId, (WebhookProviderType)999, Arg.Any<string>(), Arg.Any<string>(), "project",
                    Arg.Any<string>(), Arg.Any<IReadOnlyList<WebhookEventType>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), null)
                .Returns(call =>
                {
                    this.CreatedPath = call.ArgAt<string>(3);
                    this.CreatedScopeId = call.ArgAt<Guid?>(7);
                    return this.WebhookDto();
                });
            this._crawl.UpdateRepoFiltersAsync(this.Id, Arg.Any<IReadOnlyList<CrawlRepoFilterDto>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    this.Capture(call.ArgAt<IReadOnlyList<CrawlRepoFilterDto>>(1));
                    return true;
                });
            this._crawl.UpdateWithResultAsync(
                this.Id, null, null, null, Arg.Any<CancellationToken>(), null, false,
                Arg.Any<IReadOnlyList<CrawlRepoFilterDto>?>()).Returns(call =>
            {
                var filters = call.ArgAt<IReadOnlyList<CrawlRepoFilterDto>?>(7);
                if (filters is not null)
                {
                    this.Capture(filters);
                }

                return CrawlConfigurationUpdateResult.Updated;
            });
            this._webhooks.UpdateAsync(this.Id, null, null, null, Arg.Any<CancellationToken>(), null, false).Returns(true);
            this._webhooks.UpdateRepoFiltersAsync(this.Id, Arg.Any<IReadOnlyList<WebhookRepoFilterDto>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    this.Capture(
                        call.ArgAt<IReadOnlyList<WebhookRepoFilterDto>>(1).Select(filter =>
                            new CrawlRepoFilterDto(
                                filter.Id, filter.RepositoryName, filter.TargetBranchPatterns, filter.CanonicalSourceRef, filter.DisplayName)).ToList());
                    return true;
                });
        }

        public Guid Id { get; } = Guid.NewGuid();
        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid? ScopeId { get; }
        public string? CreatedPath { get; private set; }
        public Guid? CreatedScopeId { get; private set; }
        public IScmProviderRegistry Registry { get; } = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        public ReviewConfigurationSelectionService Selection { get; }

        public AdminCrawlConfigsController Crawl(IProviderActivationService? activation = null) => new(
            this._crawl, Substitute.For<IUserRepository>(), this._clients, this.Registry, this.Selection,
            Substitute.For<IProCursorKnowledgeSourceRepository>(), NullLogger<AdminCrawlConfigsController>.Instance,
            providerActivationService: activation)
        {
            ControllerContext = Context(),
        };

        public AdminWebhookConfigsController Webhook(IProviderActivationService? activation = null)
        {
            var generator = Substitute.For<IWebhookSecretGenerator>();
            generator.GenerateSecret().Returns("one-time-test-secret");
            var codec = Substitute.For<ISecretProtectionCodec>();
            codec.Protect(Arg.Any<string>(), "WebhookSecret").Returns("protected-test-secret");
            return new AdminWebhookConfigsController(
                this._webhooks, Substitute.For<IWebhookDeliveryLogRepository>(),
                Substitute.For<IUserRepository>(), this._clients, this.Registry, this.Selection, generator, codec,
                new ConfigurationBuilder().Build(), NullLogger<AdminWebhookConfigsController>.Instance, activation)
            {
                ControllerContext = Context(),
            };
        }

        public CreateAdminCrawlConfigRequest CrawlRequest(string state) => new(
            this.ClientId, "project", (ScmProvider)999, this.ScopeId, " https://provider.example/team ", RepoFilters: state switch
            {
                "null" => null,
                "empty" => [],
                _ => [new(" repo ", [" main ", "MAIN", " ", "release/*"], new(" custom ", " native "), " Display ")],
            });

        public CreateAdminWebhookConfigRequest WebhookRequest(string state) => new(
            this.ClientId, (WebhookProviderType)999, this.ScopeId, " https://provider.example/team ", "project", [WebhookEventType.PullRequestCreated],
            state switch
            {
                "null" => null,
                "empty" => [],
                _ => [new(" repo ", [" main ", "MAIN", " ", "release/*"], new(" custom ", " native "), " Display ")],
            });

        public void AssertFilters(string state, bool creating)
        {
            Assert.Equal(state == "populated" || !creating && state == "empty", this._replaced);
            if (state == "populated")
            {
                var filter = Assert.Single(this._filters!);
                Assert.Equal("repo", filter.RepositoryName);
                Assert.Equal("Display", filter.DisplayName);
                Assert.Equal(new CanonicalSourceReferenceDto("custom", "native"), filter.CanonicalSourceRef);
                Assert.Equal(new[] { "main", "release/*" }, filter.TargetBranchPatterns);
            }
            else if (this._replaced)
            {
                Assert.Empty(this._filters!);
            }
        }

        public void AssertNoProviderCalls()
        {
            this.Registry.DidNotReceiveWithAnyArgs().GetReviewSourcePolicy(default);
            this.Registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
            this.Registry.DidNotReceiveWithAnyArgs().IsRegistered(default);
        }

        private void Capture(IReadOnlyList<CrawlRepoFilterDto> filters)
        {
            this._filters = filters;
            this._replaced = true;
        }

        private CrawlConfigurationDto CrawlDto() => new(
            this.Id, this.ClientId, (ScmProvider)999,
            this.CreatedPath ?? "https://provider.example/team", "project", 60, true, DateTimeOffset.UtcNow,
            this._filters ?? [], this.CreatedPath is null ? this.ScopeId : this.CreatedScopeId);

        private WebhookConfigurationDto WebhookDto() => new(
            this.Id, this.ClientId, (WebhookProviderType)999, "public-path",
            this.CreatedPath ?? "https://provider.example/team", "project", true, DateTimeOffset.UtcNow, [WebhookEventType.PullRequestCreated],
            (this._filters ?? []).Select(filter => new WebhookRepoFilterDto(
                filter.Id, filter.RepositoryName, filter.TargetBranchPatterns,
                filter.CanonicalSourceRef, filter.DisplayName)).ToList(), this.CreatedPath is null ? this.ScopeId : this.CreatedScopeId);

        private static ControllerContext Context()
        {
            var http = new DefaultHttpContext();
            http.Items["IsAdmin"] = true;
            return new ControllerContext { HttpContext = http };
        }
    }
}
