// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Intake.Queries;

public sealed class ResolvePullRequestHandlerTests
{
    private static readonly Guid AdoClientId = Guid.Parse("7e2456e5-f799-4aea-b749-9bf543308780");
    private static readonly Guid ForgejoClientId = Guid.Parse("bf056eba-8c94-4245-a40e-eb6640ee1e4c");

    [Fact]
    public async Task HandleAsync_ForAzureDevOps_ResolvesProjectKeyAndRepositoryIdFromNames()
    {
        var configurations = SubstituteRepository(AdoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        var match = Assert.Single(result.Matches);
        Assert.Equal(AdoClientId, match.ClientId);
        Assert.Equal(ScmProvider.AzureDevOps, match.Provider);
        Assert.Equal("https://dev.azure.com/meister-dev", match.ProviderScopePath);
        Assert.Equal("5cda05b9-bbfa-4c44-88e9-16aa900515d2", match.ProviderProjectKey);
        Assert.Equal("c39fd3f3-e84b-4d01-84df-57964de91bc8", match.RepositoryId);
        Assert.Equal(182, match.PullRequestId);
    }

    [Fact]
    public async Task HandleAsync_ForForgejo_MatchesTheOwnerAgainstTheProjectKey()
    {
        // Forgejo keeps the host in the scope path and the owner in the project key, where Azure DevOps
        // puts the organization in the scope path. One address shape has to satisfy both.
        var configurations = SubstituteRepository(ForgejoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        var match = Assert.Single(result.Matches);
        Assert.Equal("http://localhost:8091", match.ProviderScopePath);
        Assert.Equal("local_admin", match.ProviderProjectKey);
        Assert.Equal("4", match.RepositoryId);
    }

    [Fact]
    public async Task HandleAsync_WhenRepositoryNameDiffersInCase_StillResolves()
    {
        var configurations = SubstituteRepository(AdoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://DEV.AZURE.COM",
                "Meister-Dev",
                "MEISTER-PROPR",
                182));

        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task HandleAsync_WhenOwnerDiffersOnTheSameHost_ResolvesNothing()
    {
        // Several owners share one self-hosted Forgejo host, so matching the host alone would hand a caller
        // another owner's coordinates.
        var configurations = SubstituteRepository(ForgejoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "someone_else",
                "propr",
                53));

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task HandleAsync_WhenHostDiffers_ResolvesNothing()
    {
        var configurations = SubstituteRepository(AdoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://dev.azure.example",
                "meister-dev",
                "meister-propr",
                182));

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task HandleAsync_WhenRepositoryIsNotCovered_ResolvesNothing()
    {
        var configurations = SubstituteRepository(AdoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://dev.azure.com",
                "meister-dev",
                "some-other-repository",
                182));

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task HandleAsync_WhenConfigurationCrawlsEveryRepository_ResolvesWithoutARepositoryId()
    {
        // "Covered but not addressable" is information the caller can act on, and is not the same answer as
        // "not covered".
        var configurations = SubstituteRepository(ForgejoConfiguration() with { RepoFilters = [] });
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        var match = Assert.Single(result.Matches);
        Assert.Null(match.RepositoryId);
        Assert.Equal("local_admin", match.ProviderProjectKey);
    }

    [Fact]
    public async Task HandleAsync_PrefersAnAddressableMatchOverACatchAllOne()
    {
        var configurations = SubstituteRepository(
            ForgejoConfiguration() with { Id = Guid.NewGuid(), RepoFilters = [] },
            ForgejoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal("4", result.Matches[0].RepositoryId);
        Assert.Null(result.Matches[1].RepositoryId);
    }

    [Fact]
    public async Task HandleAsync_ForAnInactiveConfiguration_StillResolvesAndSaysSo()
    {
        // Crawling being switched off does not make the repository's past reviews unreadable.
        var configurations = SubstituteRepository(AdoConfiguration() with { IsActive = false });
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        var match = Assert.Single(result.Matches);
        Assert.False(match.IsActiveConfiguration);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerSeesNoClients_ResolvesNothingWithoutReadingConfigurations()
    {
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [],
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        Assert.Empty(result.Matches);
        await configurations.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
        await configurations.DidNotReceive()
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ForAPlatformAdministrator_ReadsEveryConfiguration()
    {
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        configurations.GetAllAsync(Arg.Any<CancellationToken>()).Returns([AdoConfiguration()]);
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                null,
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        Assert.Single(result.Matches);
        await configurations.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NeverReturnsAClientTheCallerCannotSee()
    {
        // The repository call is scoped, and the handler filters again on the way out, so a repository that
        // over-returns cannot widen what the caller sees.
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        configurations
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([AdoConfiguration(), ForgejoConfiguration()]);
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task HandleAsync_WhenTwoConfigurationsResolveTheSameCoordinates_ReturnsOneMatch()
    {
        var configurations = SubstituteRepository(
            AdoConfiguration(),
            AdoConfiguration() with { Id = Guid.NewGuid(), CrawlIntervalSeconds = 900 });
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        Assert.Single(result.Matches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public async Task HandleAsync_WhenHostIsUnusable_ResolvesNothing(string hostBaseUrl)
    {
        var configurations = SubstituteRepository(AdoConfiguration());
        var sut = Handler(configurations);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                hostBaseUrl,
                "meister-dev",
                "meister-propr",
                182));

        Assert.Empty(result.Matches);
    }


    [Fact]
    public async Task HandleAsync_WhenOnlyAWebhookCoversTheRepository_StillResolves()
    {
        // A repository reaches ProPR through a crawl configuration or through a webhook, and either is
        // sufficient. Requiring a crawl configuration would refuse repositories ProPR is actively reviewing.
        var sut = Handler(
            SubstituteRepository(),
            SubstituteWebhookRepository(ForgejoWebhook()),
            SubstituteRegistry([ForgejoRepository()]));

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        var match = Assert.Single(result.Matches);
        Assert.Equal(ForgejoClientId, match.ClientId);
        Assert.Equal(ScmProvider.Forgejo, match.Provider);
        Assert.Equal("http://localhost:8091", match.ProviderScopePath);
        Assert.Equal("local_admin", match.ProviderProjectKey);
        Assert.Equal("4", match.RepositoryId);
    }

    [Fact]
    public async Task HandleAsync_TakesOnlyTheRepositoryIdentityFromDiscovery()
    {
        // Discovery reports the host authority alone, which for Azure DevOps omits the organization. Taking
        // the scope path from it would produce coordinates that disagree with what a review job carries, so
        // the configured values must survive untouched.
        var sut = Handler(
            SubstituteRepository(),
            SubstituteWebhookRepository(ForgejoWebhook()),
            SubstituteRegistry([ForgejoRepository()]));

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        var match = Assert.Single(result.Matches);
        Assert.Equal("http://localhost:8091", match.ProviderScopePath);
        Assert.Equal("local_admin", match.ProviderProjectKey);
    }

    [Fact]
    public async Task HandleAsync_PrefersARecordedIdentityOverAskingTheProvider()
    {
        var registry = SubstituteRegistry([ForgejoRepository()]);
        var sut = Handler(SubstituteRepository(AdoConfiguration()), SubstituteWebhookRepository(), registry);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [AdoClientId],
                "https://dev.azure.com",
                "meister-dev",
                "meister-propr",
                182));

        Assert.Single(result.Matches);
        registry.DidNotReceive().GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>());
    }

    [Fact]
    public async Task HandleAsync_WhenDiscoveryFails_ResolvesAsCoveredButNotAddressable()
    {
        // Discovery reaches the provider over the network with the client's credential, so it can fail for
        // reasons unrelated to this request. Losing the whole answer would be a worse outcome than losing
        // the one value it supplies.
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery
            .ListRepositoriesAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryRef>>(_ => throw new InvalidOperationException("host unreachable"));
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>()).Returns(discovery);

        var sut = Handler(SubstituteRepository(), SubstituteWebhookRepository(ForgejoWebhook()), registry);

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        var match = Assert.Single(result.Matches);
        Assert.Null(match.RepositoryId);
        Assert.Equal("local_admin", match.ProviderProjectKey);
    }

    [Fact]
    public async Task HandleAsync_WhenDiscoveryKnowsNoSuchRepository_ResolvesAsCoveredButNotAddressable()
    {
        var sut = Handler(
            SubstituteRepository(),
            SubstituteWebhookRepository(ForgejoWebhook()),
            SubstituteRegistry([]));

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        Assert.Null(Assert.Single(result.Matches).RepositoryId);
    }

    [Fact]
    public async Task HandleAsync_WhenACrawlConfigurationAndAWebhookAgree_ReturnsOneMatch()
    {
        var sut = Handler(
            SubstituteRepository(ForgejoConfiguration()),
            SubstituteWebhookRepository(ForgejoWebhook()),
            SubstituteRegistry([ForgejoRepository()]));

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task HandleAsync_WhenAWebhookNamesOtherRepositories_DoesNotClaimThisOne()
    {
        var sut = Handler(
            SubstituteRepository(),
            SubstituteWebhookRepository(ForgejoWebhook("propr-review-demo-go", "propr-review-demo-rust")),
            SubstituteRegistry([ForgejoRepository()]));

        var result = await sut.HandleAsync(
            new ResolvePullRequestQuery(
                [ForgejoClientId],
                "http://localhost:8091",
                "local_admin",
                "propr",
                53));

        Assert.Empty(result.Matches);
    }

    private static WebhookConfigurationDto ForgejoWebhook(params string[] repositoryNames)
    {
        var names = repositoryNames.Length == 0 ? ["propr"] : repositoryNames;

        return new WebhookConfigurationDto(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            ForgejoClientId,
            WebhookProviderType.Forgejo,
            "path-key",
            "http://localhost:8091",
            "local_admin",
            true,
            DateTimeOffset.UnixEpoch,
            [WebhookEventType.PullRequestCreated],
            names
                .Select((name, index) => new WebhookRepoFilterDto(
                    Guid.Parse($"6666666{index}-6666-6666-6666-666666666666"),
                    name,
                    [],
                    // A webhook is registered by name, so it records no provider identity. This is the case
                    // discovery exists to fill.
                    null,
                    name))
                .ToList());
    }

    private static RepositoryRef ForgejoRepository()
    {
        return new RepositoryRef(
            new ProviderHostRef(ScmProvider.Forgejo, "http://localhost:8091"),
            "4",
            "local_admin",
            "local_admin/propr");
    }

    /// <summary>Builds the handler with no webhook coverage and no registered provider, unless given some.</summary>
    private static ResolvePullRequestHandler Handler(
        ICrawlConfigurationRepository crawlConfigurations,
        IWebhookConfigurationRepository? webhookConfigurations = null,
        IScmProviderRegistry? providerRegistry = null)
    {
        return new ResolvePullRequestHandler(
            crawlConfigurations,
            webhookConfigurations ?? SubstituteWebhookRepository(),
            providerRegistry ?? SubstituteRegistry(null),
            NullLogger<ResolvePullRequestHandler>.Instance);
    }

    private static IWebhookConfigurationRepository SubstituteWebhookRepository(params WebhookConfigurationDto[] configurations)
    {
        var repository = Substitute.For<IWebhookConfigurationRepository>();
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(configurations);
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(configurations);
        return repository;
    }

    /// <summary>A registry whose discovery returns the given repositories, or one that is not registered.</summary>
    private static IScmProviderRegistry SubstituteRegistry(IReadOnlyList<RepositoryRef>? repositories)
    {
        var registry = Substitute.For<IScmProviderRegistry>();

        if (repositories is null)
        {
            registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(false);
            return registry;
        }

        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery
            .ListRepositoriesAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(repositories);
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>()).Returns(discovery);
        return registry;
    }

    private static ICrawlConfigurationRepository SubstituteRepository(params CrawlConfigurationDto[] configurations)
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(configurations);
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(configurations);
        return repository;
    }

    private static CrawlConfigurationDto AdoConfiguration()
    {
        return new CrawlConfigurationDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AdoClientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/meister-dev",
            "5cda05b9-bbfa-4c44-88e9-16aa900515d2",
            300,
            true,
            DateTimeOffset.UnixEpoch,
            [
                new CrawlRepoFilterDto(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "meister-propr",
                    [],
                    new CanonicalSourceReferenceDto("azureDevOps", "c39fd3f3-e84b-4d01-84df-57964de91bc8"),
                    "meister-propr"),
            ]);
    }

    private static CrawlConfigurationDto ForgejoConfiguration()
    {
        return new CrawlConfigurationDto(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ForgejoClientId,
            ScmProvider.Forgejo,
            "http://localhost:8091",
            "local_admin",
            300,
            true,
            DateTimeOffset.UnixEpoch,
            [
                new CrawlRepoFilterDto(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    "propr",
                    [],
                    new CanonicalSourceReferenceDto("forgejo", "4"),
                    "propr"),
            ]);
    }
}
