using MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling;

public sealed class ClientReviewTargetsControllerTests
{
    private readonly Guid clientId = Guid.NewGuid();
    private readonly Guid connectionId = Guid.NewGuid();
    private readonly ICrawlConfigurationRepository configurations = Substitute.For<ICrawlConfigurationRepository>();
    private readonly IClientScmConnectionRepository connections = Substitute.For<IClientScmConnectionRepository>();
    private readonly IScmProviderRegistry providers = Substitute.For<IScmProviderRegistry>();

    [Fact]
    public async Task GetOpenReviews_ReturnsBoundedOpenReviewsForConfiguredTarget()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ReviewDiscoveryItemDto>>(
                Enumerable.Range(1, 105).Select(number => ReviewItem(call.ArgAt<RepositoryRef>(1), number)).ToList()));

        var result = Assert.IsType<OkObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));

        var reviews = Assert.IsAssignableFrom<IReadOnlyList<ClientOpenReviewResponse>>(result.Value);
        Assert.Equal(100, reviews.Count);
        Assert.Equal(105, reviews[0].Number);
        Assert.Equal("Review 105", reviews[0].Title);
        Assert.Equal("https://github.example.test/owner/repo/pull/105", reviews[0].WebUrl);
        Assert.Equal("open", reviews[0].State);
        await discovery.Received(1).ListOpenReviewsAsync(
            clientId,
            Arg.Is<RepositoryRef>(repository => repository.ExternalRepositoryId == "repo-1" &&
                                                repository.OwnerOrNamespace == "owner" && repository.RepositoryName == "repo"),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOpenReviews_RejectsTargetOutsideClientBeforeProviderAccess()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));

        Assert.IsType<NotFoundResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewDiscoveryProvider(default);
    }

    [Fact]
    public async Task GetOpenReviews_RejectsUnverifiedOrMismatchedConnection()
    {
        var controller = this.CreateController();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        this.StubConnection(verificationStatus: "failed");
        Assert.IsType<BadRequestObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));

        this.StubConnection(ScmProvider.Forgejo);
        Assert.IsType<BadRequestObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewDiscoveryProvider(default);
    }

    [Fact]
    public async Task GetOpenReviews_RejectsDifferentHostOnSameProvider()
    {
        var controller = this.CreateController();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ClientScmConnectionDto?>(
                    new ClientScmConnectionDto(
                        connectionId, clientId, ScmProvider.GitHub, "https://other.example.test",
                        ScmAuthenticationKind.PersonalAccessToken, "Other", true, "verified",
                        DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        Assert.IsType<BadRequestObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewDiscoveryProvider(default);
    }

    [Fact]
    public async Task GetOpenReviews_RejectsDifferentBasePathOnSameHost()
    {
        var controller = this.CreateController();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(
                [
                    Target() with { ProviderScopePath = "https://git.example.test/forgejo-a" },
                ]));
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ClientScmConnectionDto?>(
                    new ClientScmConnectionDto(
                        connectionId, clientId, ScmProvider.GitHub, "https://git.example.test/forgejo-b",
                        ScmAuthenticationKind.PersonalAccessToken, "Other", true, "verified",
                        DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        Assert.IsType<BadRequestObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewDiscoveryProvider(default);
    }

    [Fact]
    public async Task GetOpenReviews_AcceptsEquivalentHostCasingAndTrailingSlash()
    {
        var controller = this.CreateController();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(
                [
                    Target() with { ProviderScopePath = "https://GIT.example.test/forgejo-a/" },
                ]));
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ClientScmConnectionDto?>(
                    new ClientScmConnectionDto(
                        connectionId, clientId, ScmProvider.GitHub, "https://git.example.test/forgejo-a",
                        ScmAuthenticationKind.PersonalAccessToken, "Other", true, "verified",
                        DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewDiscoveryItemDto>>([]));

        Assert.IsType<OkObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
    }

    [Fact]
    public async Task GetOpenReviews_ReturnsBadGatewayWhenProviderFails()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ReviewDiscoveryItemDto>>(new HttpRequestException("unavailable")));

        var result = Assert.IsType<ObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task GetOpenReviews_FiltersClosedReviewsAndUnsafeLinks()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ReviewDiscoveryItemDto>>(
            [
                ReviewItem(call.ArgAt<RepositoryRef>(1), 1) with { ReviewState = CodeReviewState.Closed },
                ReviewItem(call.ArgAt<RepositoryRef>(1), 2) with { WebUrl = "javascript:alert(1)", ReviewState = CodeReviewState.Draft },
            ]));

        var result = Assert.IsType<OkObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        var review = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientOpenReviewResponse>>(result.Value));
        Assert.Equal("draft", review.State);
        Assert.Null(review.WebUrl);
    }

    [Fact]
    public async Task GetRepositories_ReturnsReachableProviderRepositories()
    {
        var controller = this.CreateController();
        this.StubConnection();
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        providers.GetRepositoryDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "owner", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "owner", "owner/repo"),
            ]));

        var result = Assert.IsType<OkObjectResult>(await controller.GetRepositories(clientId, connectionId, "owner"));

        var repository = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientReviewTargetRepositoryResponse>>(result.Value));
        Assert.Equal("repo-1", repository.RepositoryId);
        Assert.Equal("repo", repository.RepositoryName);
        Assert.Equal("owner", repository.ProviderProjectKey);
    }

    [Fact]
    public async Task GetRepositories_RejectsAbsoluteNonAzureScope()
    {
        var controller = this.CreateController();
        this.StubConnection();

        var result = await controller.GetRepositories(clientId, connectionId, "https://another.example.test/owner");

        Assert.IsType<BadRequestObjectResult>(result);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Fact]
    public async Task CreateTarget_CreatesInactiveCanonicalRepositoryTarget()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
        configurations.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-1", "repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Target()));

        var result = Assert.IsType<ObjectResult>(await controller.CreateTarget(clientId, Request()));

        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        var response = Assert.IsType<ClientReviewTargetResponse>(result.Value);
        Assert.Equal("repo-1", response.RepositoryId);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task CreateTarget_RepeatedCoordinatesReturnExistingTarget()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));

        var result = Assert.IsType<OkObjectResult>(await controller.CreateTarget(clientId, Request()));

        Assert.Equal(Target().Id, Assert.IsType<ClientReviewTargetResponse>(result.Value).Id);
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task CreateTarget_ConflictingRepositoryReturnsConflict()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.GitHub);
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "owner", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "another-repo", "owner", "owner/repo"),
            ]));

        var result = Assert.IsType<ConflictObjectResult>(await controller.CreateTarget(clientId, Request() with { RepositoryId = "another-repo" }));

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }

    [Fact]
    public async Task CreateTarget_RejectsUnverifiedConnectionAndForeignOrigin()
    {
        var controller = this.CreateController();
        this.StubConnection(verificationStatus: "failed");
        Assert.IsType<BadRequestObjectResult>(await controller.CreateTarget(clientId, Request()));

        this.StubConnection();
        Assert.IsType<BadRequestObjectResult>(
            await controller.CreateTarget(clientId, Request() with { ProviderScopePath = "https://other.example.test/project" }));
    }

    [Fact]
    public async Task CreateTarget_RejectsForgedRepositoryAndProject()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));

        Assert.IsType<BadRequestObjectResult>(await controller.CreateTarget(clientId, Request() with { RepositoryId = "forged" }));
        Assert.IsType<BadRequestObjectResult>(await controller.CreateTarget(clientId, Request() with { ProviderProjectKey = "forged" }));
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task CreateTarget_RejectsAmbiguousRepositoryIdentity()
    {
        var controller = this.CreateController();
        this.StubConnection();
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.GitHub);
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "owner", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "owner", "owner/repo"),
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "owner", "owner/repo"),
            ]));

        Assert.IsType<ConflictObjectResult>(await controller.CreateTarget(clientId, Request()));
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task CreateTarget_RejectsUnregisteredAzureDevOpsScope()
    {
        var controller = this.CreateController();
        this.StubConnection(ScmProvider.AzureDevOps);
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));

        var result = await controller.CreateTarget(
            clientId,
            new CreateClientReviewTargetRequest(
                connectionId, "project", "repo-1", "repo",
                "https://dev.azure.com/unregistered"));

        Assert.IsType<BadRequestObjectResult>(result);
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task CreateTarget_RejectsAzureDevOpsScopeWithQueryOrFragment()
    {
        var controller = this.CreateController();
        this.StubConnection(ScmProvider.AzureDevOps);

        Assert.IsType<BadRequestObjectResult>(
            await controller.CreateTarget(
                clientId,
                new CreateClientReviewTargetRequest(
                    connectionId, "project", "repo-1", "repo",
                    "https://dev.azure.com/org?other=1")));
        Assert.IsType<BadRequestObjectResult>(
            await controller.CreateTarget(
                clientId,
                new CreateClientReviewTargetRequest(
                    connectionId, "project", "repo-1", "repo",
                    "https://dev.azure.com/org#other")));
    }

    [Fact]
    public async Task CreateTarget_EquivalentCaseAndTrailingSlashReturnsExistingTarget()
    {
        var controller = this.CreateController();
        this.StubConnection(ScmProvider.AzureDevOps);
        var existing = new CrawlConfigurationDto(
            Guid.NewGuid(), clientId, ScmProvider.AzureDevOps,
            "https://dev.azure.com/Org", "Project", 60, false, DateTimeOffset.UtcNow,
            [
                new CrawlRepoFilterDto(
                    Guid.NewGuid(), "repo", [],
                    new CanonicalSourceReferenceDto("AzureDevOps", "repo-1"))
            ]);
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([existing]));
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.AzureDevOps);
        discovery.ListScopesAsync(clientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/Org"]));
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "https://dev.azure.com/Org", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "Project", "Project", "repo"),
            ]));

        var result = Assert.IsType<OkObjectResult>(
            await controller.CreateTarget(
                clientId,
                new CreateClientReviewTargetRequest(
                    connectionId, "project", "repo-1", "repo",
                    "https://dev.azure.com/org/")));

        Assert.Equal(existing.Id, Assert.IsType<ClientReviewTargetResponse>(result.Value).Id);
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task GetRepositories_NestedNamespaceUsesRepositoryOwnerAsProjectKey()
    {
        var controller = this.CreateController();
        this.StubConnection();
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.GitHub);
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "group", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-2", "group/sub", "group/sub/repo"),
            ]));

        var result = Assert.IsType<OkObjectResult>(await controller.GetRepositories(clientId, connectionId, "group"));
        var repository = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientReviewTargetRepositoryResponse>>(result.Value));

        Assert.Equal("group/sub", repository.ProviderProjectKey);
        Assert.Equal("repo", repository.RepositoryName);
    }

    [Fact]
    public async Task GetRepositories_AzureDevOpsIncludesProjectNameWithoutReplacingProjectId()
    {
        var controller = this.CreateController();
        this.StubConnection(ScmProvider.AzureDevOps);
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.AzureDevOps);
        discovery.ListRepositoriesAsync(
                clientId, Arg.Any<ProviderHostRef>(),
                "https://dev.azure.com/org", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(
                    call.ArgAt<ProviderHostRef>(1), "repo-guid", "project-guid", "project-guid",
                    "my-repo", "My Project"),
            ]));

        var result = Assert.IsType<OkObjectResult>(await controller.GetRepositories(clientId, connectionId, "https://dev.azure.com/org"));
        var repository = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientReviewTargetRepositoryResponse>>(result.Value));

        Assert.Equal("project-guid", repository.ProviderProjectKey);
        Assert.Equal("My Project", repository.ProviderProjectDisplayName);
        Assert.Equal("my-repo", repository.RepositoryName);
    }

    [Fact]
    public async Task CreateTarget_PersistsDiscoveredProjectCasing()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.GitHub);
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "owner", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "Owner", "Owner/repo"),
            ]));
        configurations.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub,
                "https://github.example.test", "Owner", "repo-1", "repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Target() with { ProviderProjectKey = "Owner" }));

        Assert.IsType<ObjectResult>(await controller.CreateTarget(clientId, Request()));
        await configurations.Received(1).AddReviewTargetAsync(
            clientId, ScmProvider.GitHub,
            "https://github.example.test", "Owner", "repo-1", "repo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTarget_PersistsConfiguredAzureDevOpsScopeAndProject()
    {
        var controller = this.CreateController();
        this.StubConnection(ScmProvider.AzureDevOps);
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
        var discovery = providers.GetRepositoryDiscoveryProvider(ScmProvider.AzureDevOps);
        discovery.ListScopesAsync(clientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["https://dev.azure.com/Org"]));
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "https://dev.azure.com/Org", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "Project", "Project", "repo"),
            ]));
        configurations.AddReviewTargetAsync(
                clientId, ScmProvider.AzureDevOps,
                "https://dev.azure.com/Org", "Project", "repo-1", "repo", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new CrawlConfigurationDto(
                        Guid.NewGuid(), clientId,
                        ScmProvider.AzureDevOps, "https://dev.azure.com/Org", "Project", 60, false,
                        DateTimeOffset.UtcNow,
                        [
                            new CrawlRepoFilterDto(
                                Guid.NewGuid(), "repo", [],
                                new CanonicalSourceReferenceDto("AzureDevOps", "repo-1"))
                        ])));

        Assert.IsType<ObjectResult>(
            await controller.CreateTarget(
                clientId,
                new CreateClientReviewTargetRequest(
                    connectionId, "project", "repo-1", "repo",
                    "https://dev.azure.com/org/")));
        await configurations.Received(1).AddReviewTargetAsync(
            clientId, ScmProvider.AzureDevOps,
            "https://dev.azure.com/Org", "Project", "repo-1", "repo", Arg.Any<CancellationToken>());
    }

    private ClientReviewTargetsController CreateController()
    {
        var context = new DefaultHttpContext();
        context.Items["IsAdmin"] = true;
        return new ClientReviewTargetsController(configurations, connections, providerRegistry: providers)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private void StubConnection(
        ScmProvider provider = ScmProvider.GitHub,
        string verificationStatus = "verified")
    {
        var hostUrl = provider == ScmProvider.AzureDevOps
            ? "https://dev.azure.com"
            : "https://github.example.test";
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ClientScmConnectionDto?>(
                    new ClientScmConnectionDto(
                        connectionId, clientId, provider, hostUrl,
                        ScmAuthenticationKind.PersonalAccessToken, "GitHub", true, verificationStatus,
                        DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        providers.GetRepositoryDiscoveryProvider(provider).Returns(discovery);
        discovery.ListScopesAsync(clientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        discovery.ListRepositoriesAsync(clientId, Arg.Any<ProviderHostRef>(), "owner", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RepositoryRef>>(
            [
                new RepositoryRef(call.ArgAt<ProviderHostRef>(1), "repo-1", "owner", "owner/repo"),
            ]));
    }

    private CreateClientReviewTargetRequest Request() =>
        new(connectionId, "owner", "repo-1", "repo");

    private static ReviewDiscoveryItemDto ReviewItem(RepositoryRef repository, int number) => new(
        ScmProvider.GitHub,
        repository,
        new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, number.ToString(), number),
        CodeReviewState.Open,
        null,
        null,
        $"Review {number}",
        $"https://github.example.test/owner/repo/pull/{number}",
        null,
        null);

    private CrawlConfigurationDto Target() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), clientId, ScmProvider.GitHub,
        "https://github.example.test", "owner", 60, false, DateTimeOffset.UtcNow,
        [new CrawlRepoFilterDto(Guid.NewGuid(), "repo", [], new CanonicalSourceReferenceDto("GitHub", "repo-1"))]);
}
