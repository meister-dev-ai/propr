// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.TestSupport;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Npgsql;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling;

public sealed class ClientReviewTargetsControllerTests
{
    public ClientReviewTargetsControllerTests()
    {
        providers.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedTargetReadRequiresLocalSourcePolicy(bool metadata)
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        providers.GetReviewSourcePolicy(ScmProvider.GitHub).Throws(new InvalidOperationException("The local policy is unavailable."));
        var controller = this.CreateController();

        var result = metadata
            ? await controller.GetOpenReviewMetadata(clientId, Target().Id, 7, connectionId)
            : await controller.GetOpenReviews(clientId, Target().Id, connectionId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyProviderThrottleReturnsControlledBadGateway(bool metadata)
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var type = typeof(MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration.ClientPullRequestOverviewService).Assembly
            .GetType("MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderThrottledException", true)!;
        var failure = (Exception)Activator.CreateInstance(type, "private throttle detail")!;
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        var overview = Substitute.For<IReviewOverviewProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        providers.GetReviewOverviewProvider(ScmProvider.GitHub).Returns(overview);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .ThrowsAsync(failure);
        overview.GetOverviewAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var result = Assert.IsType<ObjectResult>(
            metadata
                ? await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId)
                : await this.CreateController().GetOpenReviews(clientId, Target().Id, connectionId));
        Assert.Equal(502, result.StatusCode);
        Assert.DoesNotContain("private throttle detail", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task Overview_RejectsRowNavigationWithoutCursorBeforeCallingTheService()
    {
        var overview = Substitute.For<IClientPullRequestOverviewService>();
        var context = new DefaultHttpContext();
        context.Items["IsAdmin"] = true;
        var controller = new ClientReviewTargetsController(configurations, connections, providerRegistry: providers, scopes: scopes, overview: overview)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        Assert.IsType<BadRequestResult>(await controller.GetOverview(clientId, new([], "binding", Page: 2)));
        await overview.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default);
    }

    [Fact]
    public async Task Overview_ReturnsRichPageThroughOneServiceCallWithoutLegacyMetadataReads()
    {
        var overview = Substitute.For<IClientPullRequestOverviewService>();
        var now = DateTimeOffset.UtcNow;
        var page = new ClientPullRequestOverviewPage([], [], "snapshot", null, 1, 25, 0, 0, 0, now, now.AddMinutes(15), false, null);
        var request = new ClientPullRequestOverviewRequest([], "binding");
        overview.ReadAsync(clientId, request, Arg.Any<CancellationToken>()).Returns(page);
        var context = new DefaultHttpContext();
        context.Items["IsAdmin"] = true;
        var controller = new ClientReviewTargetsController(configurations, connections, providerRegistry: providers, scopes: scopes, overview: overview)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        Assert.Same(page, Assert.IsType<OkObjectResult>(await controller.GetOverview(clientId, request)).Value);
        await overview.Received(1).ReadAsync(clientId, request, Arg.Any<CancellationToken>());
        providers.DidNotReceiveWithAnyArgs().GetReviewOverviewProvider(default);
        providers.DidNotReceiveWithAnyArgs().GetReviewDiscoveryProvider(default);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 403)]
    public async Task Overview_RejectsUnauthenticatedAndForeignClientCallersBeforeServiceReads(bool authenticated, int expected)
    {
        var overview = Substitute.For<IClientPullRequestOverviewService>();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        var context = new DefaultHttpContext();
        if (authenticated)
        {
            context.Items["UserId"] = Guid.NewGuid().ToString();
            context.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [Guid.NewGuid()] = ClientRole.ClientAdministrator };
        }

        var controller = new ClientReviewTargetsController(configurations, connections, licensing, providers, scopes, overview)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        Assert.Equal(expected, Assert.IsType<ObjectResult>(await controller.GetOverview(clientId, new([], "binding"))).StatusCode);
        Assert.Empty(overview.ReceivedCalls());
        Assert.Empty(licensing.ReceivedCalls());
    }

    [Fact]
    public async Task Overview_RechecksCapabilityOnEachAuthorizedRequestBeforeServiceReads()
    {
        var overview = Substitute.For<IClientPullRequestOverviewService>();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        var available = true;
        licensing.GetCapabilityAsync(PremiumCapabilityKey.CrawlConfigs, Arg.Any<CancellationToken>()).Returns(_ =>
            new CapabilitySnapshot(PremiumCapabilityKey.CrawlConfigs, "Crawl configurations", true, PremiumCapabilityOverrideState.Default, available, null));
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();
        context.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [clientId] = ClientRole.ClientUser };
        var controller = new ClientReviewTargetsController(configurations, connections, licensing, providers, scopes, overview)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        Assert.IsType<OkObjectResult>(await controller.GetOverview(clientId, new([], "binding")));
        available = false;
        Assert.Equal(409, Assert.IsType<PremiumFeatureUnavailableResult>(await controller.GetOverview(clientId, new([], "binding"))).StatusCode);
        Assert.Single(overview.ReceivedCalls());
        await licensing.Received(2).GetCapabilityAsync(PremiumCapabilityKey.CrawlConfigs, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManagementTarget_ReadsRemovedOwnedMetadataWithoutProviderAccess()
    {
        var target = Target() with { ReviewTargetLifecycle = ReviewTargetLifecycle.Removed, ReviewTargetRevision = 9007199254740993 };
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(target);
        var response = Assert.IsType<OkObjectResult>(await this.CreateController().GetManagementTarget(clientId, target.Id));
        var saved = Assert.IsType<ClientReviewTargetResponse>(response.Value);
        Assert.Equal("removed", saved.Lifecycle);
        Assert.Equal("9007199254740993", saved.Revision);
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Fact]
    public async Task ManagementTarget_RejectsForeignOwnedMetadata()
    {
        var target = Target() with { ClientId = Guid.NewGuid() };
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(target);
        Assert.IsType<NotFoundResult>(await this.CreateController().GetManagementTarget(clientId, target.Id));
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task ManagementPage_ForwardsSavedMetadataSearchWithoutConnectionReads()
    {
        var target = Target();
        configurations.GetManagementTargetPageAsync(clientId, "owner", null, null, 1, 25, Arg.Any<CancellationToken>())
            .Returns(new CrawlConfigurationPageDto([target], 1, 1, 25, new string('a', 64)));
        var response = Assert.IsType<OkObjectResult>(await this.CreateController().GetManagementTargets(clientId, search: "owner"));
        var page = Assert.IsType<ClientReviewTargetPageResponse>(response.Value);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(new string('a', 64), page.SnapshotVersion);
        Assert.Single(page.Items);
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("github ")]
    [InlineData(" github")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("unknown")]
    public async Task ManagementPage_RejectsNoncanonicalProviderBeforeQuery(string provider)
    {
        Assert.IsType<BadRequestObjectResult>(await this.CreateController().GetManagementTargets(clientId, provider: provider));
        await configurations.DidNotReceiveWithAnyArgs().GetManagementTargetPageAsync(default, null, null, null, default, default);
    }

    [Theory]
    [InlineData("azureDevOps", ScmProvider.AzureDevOps)]
    [InlineData("github", ScmProvider.GitHub)]
    [InlineData("gitLab", ScmProvider.GitLab)]
    [InlineData("forgejo", ScmProvider.Forgejo)]
    public async Task ManagementPage_MapsExactProviderWithoutActivationOrDiscovery(string input, ScmProvider provider)
    {
        var target = Target() with { Provider = provider, IsActive = false, ReviewTargetLifecycle = ReviewTargetLifecycle.Disabled };
        configurations.GetManagementTargetPageAsync(clientId, null, provider, null, 1, 25, Arg.Any<CancellationToken>())
            .Returns(new CrawlConfigurationPageDto([target], 1, 1, 25, new string('a', 64)));
        var response = Assert.IsType<OkObjectResult>(await this.CreateController().GetManagementTargets(clientId, provider: input));
        Assert.Equal(input, Assert.Single(Assert.IsType<ClientReviewTargetPageResponse>(response.Value).Items).ProviderFamily);
        providers.DidNotReceiveWithAnyArgs().IsRegistered(default);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task LifecycleChange_UsesConditionalOwnedWriteWithoutProviderAccess()
    {
        var target = Target();
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(target);
        configurations.ChangeReviewTargetLifecycleAsync(target.Id, clientId, 1, ReviewTargetLifecycle.Disabled, Arg.Any<CancellationToken>()).Returns(true);
        Assert.IsType<OkObjectResult>(await this.CreateController().ChangeLifecycle(clientId, target.Id, new("1", "disabled")));
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Theory]
    [InlineData("https://dev.azure.com/other", false, false)]
    [InlineData("https://dev.azure.com/org", true, false)]
    [InlineData("https://dev.azure.com/org", false, true)]
    public async Task UpdateTargetPolicy_RequiresConfiguredOrganizationOnExactSelectedConnection(string scope, bool foreignConnection, bool accepted)
    {
        this.StubConnection(ScmProvider.AzureDevOps);
        var target = Target() with { Provider = ScmProvider.AzureDevOps, ProviderScopePath = "https://dev.azure.com/org" };
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([target]);
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(
            target with
            {
                RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main"] }]
            });
        scopes.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
        [
            new ClientScmScopeDto(
                Guid.NewGuid(), clientId, foreignConnection ? Guid.NewGuid() : connectionId,
                "organization", "org", scope, "org", "verified", true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        configurations.UpdateReviewTargetPolicyAsync(
            target, clientId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        var result = await this.CreateController().UpdateTargetPolicy(
            clientId, target.Id, new UpdateClientReviewTargetPolicyRequest(connectionId, [], ["main"]));
        if (accepted)
        {
            Assert.IsType<OkObjectResult>(result);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            await configurations.DidNotReceiveWithAnyArgs().UpdateReviewTargetPolicyAsync(default!, default, default!, default!);
        }

        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionByIdAsync(default, default);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UpdateTargetPolicy_ReturnsAuthoritativeConcurrentActivation(bool initialActivation, bool currentActivation)
    {
        this.StubConnection();
        var target = Target() with { IsActive = initialActivation };
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([target]);
        configurations.UpdateReviewTargetPolicyAsync(
            target, clientId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(
            target with
            {
                IsActive = currentActivation, RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main"] }]
            });
        var result = Assert.IsType<OkObjectResult>(
            await this.CreateController().UpdateTargetPolicy(clientId, target.Id, new UpdateClientReviewTargetPolicyRequest(connectionId, [], ["main"])));
        Assert.Equal(currentActivation, Assert.IsType<ClientReviewTargetResponse>(result.Value).IsActive);
    }

    [Fact]
    public async Task UpdateTargetPolicy_PreservesTargetIdentityAndActivationAndNormalizesReplacement()
    {
        this.StubConnection();
        var target = Target() with { IsActive = true };
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(
            target with
            {
                RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main", "release/*"] }]
            });
        providers.ClearReceivedCalls();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([target]);
        configurations.UpdateReviewTargetPolicyAsync(
            target, clientId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = Assert.IsType<OkObjectResult>(
            await this.CreateController().UpdateTargetPolicy(
                clientId, target.Id, new UpdateClientReviewTargetPolicyRequest(connectionId, [], ["refs/heads/main", "release/*"])));

        var response = Assert.IsType<ClientReviewTargetResponse>(result.Value);
        Assert.Equal(target.Id, response.Id);
        Assert.True(response.IsActive);
        Assert.Equal("repo-1", response.RepositoryId);
        Assert.Equal(["main", "release/*"], response.TargetBranchPatterns);
        await configurations.Received(1).UpdateReviewTargetPolicyAsync(
            target, clientId, Arg.Is<IReadOnlyList<string>>(patterns => patterns.Count == 0),
            Arg.Is<IReadOnlyList<string>>(patterns => patterns.SequenceEqual(new[] { "main", "release/*" })), Arg.Any<CancellationToken>());
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Fact]
    public async Task UpdateTargetPolicy_ConflictingExpectedPolicyReturnsConflictWithoutOtherWrites()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        configurations.UpdateReviewTargetPolicyAsync(
                Arg.Any<CrawlConfigurationDto>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        Assert.IsType<ConflictObjectResult>(
            await this.CreateController().UpdateTargetPolicy(
                clientId, Target().Id, new UpdateClientReviewTargetPolicyRequest(connectionId, ["main"], ["release/*"])));
        await configurations.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default, default);
        await configurations.DidNotReceiveWithAnyArgs().SetActiveAsync(default, default, default);
    }

    [Fact]
    public async Task UpdateTargetPolicy_RejectsForeignTargetBeforeConnectionAndProviderAccess()
    {
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target() with { ClientId = Guid.NewGuid() }]);
        Assert.IsType<NotFoundResult>(
            await this.CreateController().UpdateTargetPolicy(clientId, Target().Id, new UpdateClientReviewTargetPolicyRequest(connectionId, [], ["main"])));
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
        await configurations.DidNotReceiveWithAnyArgs().UpdateReviewTargetPolicyAsync(default!, default, default!, default!);
    }

    [Fact]
    public async Task UpdateTargetPolicy_RejectsUnauthenticatedCallerBeforeStorageAccess()
    {
        var controller = this.CreateController();
        controller.HttpContext.Items.Clear();
        var denied = Assert.IsType<ObjectResult>(
            await controller.UpdateTargetPolicy(clientId, Target().Id, new UpdateClientReviewTargetPolicyRequest(connectionId, [], ["main"])));
        Assert.Equal(401, denied.StatusCode);
        await configurations.DidNotReceiveWithAnyArgs().GetByClientAsync(default);
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task UpdateTargetPolicy_RejectsForeignConnectionBeforeMutationOrProviderAccess()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var selected = (await connections.GetByIdAsync(clientId, connectionId))!;
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(selected with { ClientId = Guid.NewGuid() });
        providers.ClearReceivedCalls();
        Assert.IsType<BadRequestObjectResult>(
            await this.CreateController().UpdateTargetPolicy(clientId, Target().Id, new UpdateClientReviewTargetPolicyRequest(connectionId, [], ["main"])));
        await configurations.DidNotReceiveWithAnyArgs().UpdateReviewTargetPolicyAsync(default!, default, default!, default!);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
    }

    [Fact]
    public async Task CreateTarget_NewPolicyCannotOverwriteAnExistingTarget()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        Assert.IsType<ConflictObjectResult>(await this.CreateController().CreateTarget(clientId, Request() with { TargetBranchPatterns = ["main"] }));
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
        await configurations.DidNotReceiveWithAnyArgs().UpdateReviewTargetPolicyAsync(default!, default, default!, default!);
    }

    [Fact]
    public async Task CreateTarget_PersistsNormalizedDestinationPolicyWithInactiveTarget()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([]);
        var target = Target();
        configurations.AddReviewTargetAsync(
            clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-1", "repo",
            Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>>()).Returns(
            target with
            {
                RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main", "release/*"] }]
            });
        var result = Assert.IsType<ObjectResult>(
            await this.CreateController().CreateTarget(clientId, Request() with { TargetBranchPatterns = ["refs/heads/main", "release/*"] }));
        Assert.Equal(201, result.StatusCode);
        var response = Assert.IsType<ClientReviewTargetResponse>(result.Value);
        Assert.False(response.IsActive);
        Assert.Equal(["main", "release/*"], response.TargetBranchPatterns);
    }

    [Fact]
    public async Task CreateTarget_RejectsPatternCountAndLengthBeforeConnectionAccess()
    {
        var controller = this.CreateController();
        Assert.IsType<BadRequestObjectResult>(await controller.CreateTarget(clientId, Request() with { TargetBranchPatterns = [new string('a', 513)] }));
        Assert.IsType<BadRequestObjectResult>(
            await controller.CreateTarget(
                clientId, Request() with
                {
                    TargetBranchPatterns = Enumerable.Range(0, 101).Select(i => "branch" + i).ToArray()
                }));
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }

    [Fact]
    public async Task GetTargets_ReturnsStoredDestinationPolicy()
    {
        var target = Target();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns(
        [
            target with
            {
                RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main", "release/*"] }],
            }
        ]);
        var result = Assert.IsType<OkObjectResult>(await this.CreateController().GetTargets(clientId));
        var row = System.Text.Json.JsonSerializer.SerializeToElement(result.Value)[0];
        Assert.Equal(["main", "release/*"], row.GetProperty("TargetBranchPatterns").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("[\"\"]")]
    [InlineData("[\"   \"]")]
    [InlineData("[\"main\\nbranch\"]")]
    [InlineData("[\"main\",\"refs/heads/MAIN\"]")]
    public async Task CreateTarget_InvalidDestinationPolicyDoesNotAccessConnectionOrProvider(string patternsJson)
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([]);
        configurations.AddReviewTargetAsync(
            clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-1", "repo", Arg.Any<CancellationToken>()).Returns(Target());
        connections.ClearReceivedCalls();
        providers.ClearReceivedCalls();
        var requestJson = System.Text.Json.JsonSerializer.Serialize(Request());
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateClientReviewTargetRequest>(
            requestJson[..^1] + ",\"TargetBranchPatterns\":" + patternsJson + "}")!;

        Assert.IsType<BadRequestObjectResult>(await this.CreateController().CreateTarget(clientId, request));
        await connections.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        providers.DidNotReceiveWithAnyArgs().GetRepositoryDiscoveryProvider(default);
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task GetOpenReviews_FiltersDestinationBranchesBeforeTruncatingCandidates()
    {
        this.StubConnection();
        var target = Target();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns(
        [
            target with
            {
                RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main", "release/*"] }],
            }
        ]);
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(call => Enumerable.Range(1, 110).Select(number => ReviewItem(call.ArgAt<RepositoryRef>(1), number) with
            {
                TargetBranch = number == 1 ? "refs/heads/main" : number == 110 ? "release/v1" : "feature/unselected",
            }).ToArray());

        var result = Assert.IsType<OkObjectResult>(await this.CreateController().GetOpenReviews(clientId, target.Id, connectionId));

        var rows = Assert.IsAssignableFrom<IReadOnlyList<ClientOpenReviewResponse>>(result.Value);
        Assert.Equal([110, 1], rows.Select(row => row.Number));
    }

    [Fact]
    public async Task GetOpenReviews_RestrictedPolicyExcludesUnavailableBranch()
    {
        this.StubConnection();
        var target = Target();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns(
        [
            target with
            {
                RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main"] }],
            }
        ]);
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(call => new[] { ReviewItem(call.ArgAt<RepositoryRef>(1), 42) });

        var result = Assert.IsType<OkObjectResult>(await this.CreateController().GetOpenReviews(clientId, target.Id, connectionId));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ClientOpenReviewResponse>>(result.Value));
    }

    [Fact]
    public async Task GetOpenReviewMetadata_UsesClientTargetAndSelectedConnection()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var overview = Substitute.For<IReviewOverviewProvider>();
        providers.GetReviewOverviewProvider(ScmProvider.GitHub).Returns(overview);
        overview.GetOverviewAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewOverviewDto(5, 1, 1, true, true));
        var result = Assert.IsType<OkObjectResult>(await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
        Assert.Equal(new ReviewOverviewDto(5, 1, 1, true, true), result.Value);
        await overview.Received(1).GetOverviewAsync(
            clientId,
            Arg.Is<CodeReviewRef>(review => review.Number == 42 && review.Repository.ExternalRepositoryId == "repo-1"),
            Arg.Is<ReviewDiscoveryContext>(context => context.ConnectionId == connectionId &&
                                                      context.ProviderScopePath == Target().ProviderScopePath), Arg.Any<CancellationToken>());
        providers.DidNotReceiveWithAnyArgs().GetReviewDiscoveryProvider(default);
    }

    [Fact]
    public async Task GetOpenReviewMetadata_RejectsForeignTargetBeforeProviderAccess()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([]);
        Assert.IsType<NotFoundResult>(await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewOverviewProvider(default);
    }

    [Theory]
    [InlineData("failed", ScmProvider.GitHub)]
    [InlineData("verified", ScmProvider.Forgejo)]
    public async Task GetOpenReviewMetadata_RejectsInvalidSelectedConnection(string status, ScmProvider provider)
    {
        this.StubConnection(provider, status);
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        Assert.IsType<BadRequestObjectResult>(await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewOverviewProvider(default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetOpenReviewMetadata_RejectsNonPositiveNumber(int number)
    {
        Assert.IsType<BadRequestObjectResult>(await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, number, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewOverviewProvider(default);
    }

    [Fact]
    public async Task GetOpenReviewMetadata_ReturnsFixedFailureAndPropagatesCancellation()
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var overview = Substitute.For<IReviewOverviewProvider>();
        providers.GetReviewOverviewProvider(ScmProvider.GitHub).Returns(overview);
        overview.GetOverviewAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("private provider detail"));
        var result = Assert.IsType<ObjectResult>(await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
        Assert.Equal(502, result.StatusCode);
        Assert.DoesNotContain("private provider detail", System.Text.Json.JsonSerializer.Serialize(result.Value));
        overview.GetOverviewAsync(clientId, Arg.Any<CodeReviewRef>(), Arg.Any<ReviewDiscoveryContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
    }

    [Fact]
    public async Task GetOpenReviewMetadata_RejectsUnauthenticatedClientAccess()
    {
        var controller = this.CreateController();
        controller.HttpContext.Items.Clear();
        var result = Assert.IsType<ObjectResult>(await controller.GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        providers.DidNotReceiveWithAnyArgs().GetReviewOverviewProvider(default);
    }

    [Fact]
    public async Task GetOpenReviews_ExposesBranchesAndNativeRevision()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(call => new[]
            {
                ReviewItem(call.ArgAt<RepositoryRef>(1), 42) with
                {
                    SourceBranch = "feature/overview", TargetBranch = "main",
                    AuthorName = "Native Author",
                    ReviewRevision = new ReviewRevision("head", "base", null, "head", null),
                },
            });
        var response = Assert.IsType<OkObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        var row = System.Text.Json.JsonSerializer.SerializeToElement(response.Value)[0];
        Assert.True(row.TryGetProperty("SourceBranch", out var source));
        Assert.Equal("feature/overview", source.GetString());
        Assert.Equal("main", row.GetProperty("TargetBranch").GetString());
        Assert.Equal("head", row.GetProperty("HeadSha").GetString());
        Assert.Equal("head", row.GetProperty("ProviderRevisionId").GetString());
        Assert.Equal("Native Author", row.GetProperty("AuthorName").GetString());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//provider.example/pull/42")]
    [InlineData("https://user:password@provider.example/pull/42")]
    [InlineData("data:text/html,unsafe")]
    public async Task GetOpenReviews_RemovesUnsafeLinksAndEmailAuthors(string url)
    {
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(call => new[] { ReviewItem(call.ArgAt<RepositoryRef>(1), 42) with { WebUrl = url, AuthorName = "private@example.test" } });
        var result = Assert.IsType<OkObjectResult>(await this.CreateController().GetOpenReviews(clientId, Target().Id, connectionId));
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientOpenReviewResponse>>(result.Value));
        Assert.Null(item.WebUrl);
        Assert.Null(item.AuthorName);
    }

    [Fact]
    public async Task GetOpenReviewMetadata_RejectsForeignConnectionAndTargetReturnedByRepository()
    {
        this.StubConnection();
        var target = Target() with { ClientId = Guid.NewGuid() };
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([target]);
        Assert.IsType<NotFoundResult>(await this.CreateController().GetOpenReviewMetadata(clientId, target.Id, 42, connectionId));
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var selected = await connections.GetByIdAsync(clientId, connectionId);
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(selected! with { ClientId = Guid.NewGuid() });
        Assert.IsType<BadRequestObjectResult>(await this.CreateController().GetOpenReviewMetadata(clientId, Target().Id, 42, connectionId));
        providers.DidNotReceiveWithAnyArgs().GetReviewOverviewProvider(default);
    }

    private readonly Guid clientId = Guid.NewGuid();
    private readonly Guid connectionId = Guid.NewGuid();
    private readonly ICrawlConfigurationRepository configurations = Substitute.For<ICrawlConfigurationRepository>();
    private readonly IClientScmConnectionRepository connections = Substitute.For<IClientScmConnectionRepository>();
    private readonly IClientScmScopeRepository scopes = Substitute.For<IClientScmScopeRepository>();
    private readonly IScmProviderRegistry providers = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();

    [Fact]
    public async Task GetTargets_SameProjectRepositoriesRemainIndividuallyAddressable()
    {
        var controller = this.CreateController();
        this.StubConnection();
        var second = Target() with
        {
            Id = Guid.NewGuid(),
            RepoFilters = [new CrawlRepoFilterDto(Guid.NewGuid(), "second", [], new CanonicalSourceReferenceDto("GitHub", "repo-2"))],
        };
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns([Target(), second, Target() with { Id = Guid.NewGuid(), RepoFilters = [] }]);
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(call => Task.FromResult<IReadOnlyList<ReviewDiscoveryItemDto>>([ReviewItem(call.ArgAt<RepositoryRef>(1), 1)]));

        var listed = Assert.IsType<OkObjectResult>(await controller.GetTargets(clientId));
        var targets = Assert.IsAssignableFrom<IReadOnlyList<ClientReviewTargetResponse>>(listed.Value);
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target => target.Id == second.Id && target.RepositoryId == "repo-2");
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            targets,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("github", json[0].GetProperty("providerFamily").GetString());
        Assert.IsType<OkObjectResult>(await controller.GetOpenReviews(clientId, second.Id, connectionId));
        await discovery.Received(1).ListOpenReviewsAsync(
            clientId, Arg.Is<RepositoryRef>(repository => repository.ExternalRepositoryId == "repo-2"), null,
            Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>());
    }

    [Fact]
    public async Task GetOpenReviews_ReturnsBoundedOpenReviewsForConfiguredTarget()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([Target()]));
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
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
            null, Arg.Any<CancellationToken>(),
            Arg.Is<ReviewDiscoveryContext>(context => context.ConnectionId == connectionId && context.ProviderScopePath == Target().ProviderScopePath));
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
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
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
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .Returns(Task.FromException<IReadOnlyList<ReviewDiscoveryItemDto>>(new InvalidOperationException("private detail")));

        var result = Assert.IsType<ObjectResult>(await controller.GetOpenReviews(clientId, Target().Id, connectionId));
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        Assert.DoesNotContain("private detail", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task GetOpenReviews_PropagatesProviderCancellation()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target()]);
        var discovery = Substitute.For<IReviewDiscoveryProvider>();
        providers.GetReviewDiscoveryProvider(ScmProvider.GitHub).Returns(discovery);
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.GetOpenReviews(clientId, Target().Id, connectionId));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "azureDevOps")]
    [InlineData(ScmProvider.GitHub, "github")]
    [InlineData(ScmProvider.GitLab, "gitLab")]
    [InlineData(ScmProvider.Forgejo, "forgejo")]
    public async Task GetTargets_ProjectsCanonicalProviderFamily(ScmProvider provider, string expected)
    {
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target() with { Provider = provider }]);
        var result = Assert.IsType<OkObjectResult>(await this.CreateController().GetTargets(clientId));
        var target = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ClientReviewTargetResponse>>(result.Value));
        Assert.Equal(expected, target.ProviderFamily);
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
        discovery.ListOpenReviewsAsync(clientId, Arg.Any<RepositoryRef>(), null, Arg.Any<CancellationToken>(), Arg.Any<ReviewDiscoveryContext>())
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
    public async Task CreateTarget_AnotherRepositoryInSameProjectCreatesIndependentTarget()
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

        var second = Target() with
        {
            Id = Guid.NewGuid(),
            RepoFilters = [new CrawlRepoFilterDto(Guid.NewGuid(), "repo", [], new CanonicalSourceReferenceDto("GitHub", "another-repo"))],
        };
        configurations.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "another-repo", "repo", Arg.Any<CancellationToken>())
            .Returns(second);
        var result = Assert.IsType<ObjectResult>(await controller.CreateTarget(clientId, Request() with { RepositoryId = "another-repo" }));

        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        Assert.Equal(second.Id, Assert.IsType<ClientReviewTargetResponse>(result.Value).Id);
    }

    [Fact]
    public async Task CreateTarget_GenericConfigurationDoesNotPreventIndividualTarget()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target() with { RepoFilters = [] }]);
        configurations.AddReviewTargetAsync(
            clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-1", "repo", Arg.Any<CancellationToken>()).Returns(Target());

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(await controller.CreateTarget(clientId, Request())).StatusCode);
    }

    [Fact]
    public async Task CreateTarget_DuplicateCanonicalTargetsReturnConflict()
    {
        var controller = this.CreateController();
        this.StubConnection();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([Target(), Target() with { Id = Guid.NewGuid() }]);

        Assert.IsType<ConflictObjectResult>(await controller.CreateTarget(clientId, Request()));
        await configurations.DidNotReceiveWithAnyArgs().AddReviewTargetAsync(default, default, default!, default!, default!, default!);
    }

    [Fact]
    public async Task CreateTarget_UniqueRaceReturnsMatchingRepositoryAmongSameProjectTargets()
    {
        var controller = this.CreateController();
        this.StubConnection();
        var unrelated = Target() with
        {
            Id = Guid.NewGuid(),
            RepoFilters = [new CrawlRepoFilterDto(Guid.NewGuid(), "another", [], new CanonicalSourceReferenceDto("GitHub", "another-id"))],
        };
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]), Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([unrelated, Target()]));
        configurations.AddReviewTargetAsync(
                clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-1", "repo", Arg.Any<CancellationToken>())
            .ThrowsAsync(
                new DbUpdateException("Unique constraint", new PostgresException("Unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));

        var result = Assert.IsType<OkObjectResult>(await controller.CreateTarget(clientId, Request()));

        Assert.Equal(Target().Id, Assert.IsType<ClientReviewTargetResponse>(result.Value).Id);
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
        return new ClientReviewTargetsController(configurations, connections, providerRegistry: providers, scopes: scopes)
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
