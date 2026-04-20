// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Unit tests for <see cref="AdoAssignedPrFetcher" />.
///     A <see cref="GitHttpClient" /> substitute is injected via
///     <see cref="AdoAssignedPrFetcher.GitClientResolver" /> to avoid a real ADO connection.
/// </summary>
public sealed class AdoAssignedPrFetcherTests
{
    private static readonly CrawlConfigurationDto DefaultConfig = new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ScmProvider.AzureDevOps,
        "https://dev.azure.com/testorg",
        "TestProject",
        Guid.NewGuid(),
        60,
        true,
        DateTimeOffset.UtcNow,
        []);

    private static AdoAssignedPrFetcher BuildSut(GitHttpClient gitClient)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));
        var fetcher = new AdoAssignedPrFetcher(
            factory,
            connectionRepository,
            NullLogger<AdoAssignedPrFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        return fetcher;
    }

    private static GitPullRequest MakePr(int prId, Guid repoId)
    {
        var pr = new GitPullRequest
        {
            PullRequestId = prId,
            Repository = new GitRepository { Id = repoId },
        };
        return pr;
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_EmptyResult_ReturnsEmptyList()
    {
        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequest>()));

        var sut = BuildSut(gitClient);
        var result = await sut.ListAssignedOpenReviewsAsync(DefaultConfig);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_IterationFetchFails_SkipsPr()
    {
        var repoId1 = Guid.NewGuid();
        var repoId2 = Guid.NewGuid();

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<GitPullRequest>
                    {
                        MakePr(11, repoId1),
                        MakePr(22, repoId2),
                    }));

        // First call throws, second succeeds
        gitClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                repoId1.ToString(),
                Arg.Any<int>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<GitPullRequestIteration>>(new Exception("ADO 500")));

        gitClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                repoId2.ToString(),
                Arg.Any<int>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequestIteration> { new() { Id = 2 } }));

        var sut = BuildSut(gitClient);
        var result = await sut.ListAssignedOpenReviewsAsync(DefaultConfig);

        // Only the second PR is included; first is skipped due to exception
        Assert.Single(result);
        Assert.Equal(22, result[0].CodeReview.Number);
    }

    // T035 — config with null ReviewerId is skipped and returns empty list

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_NullReviewerId_ReturnsEmptyListWithoutCallingAdo()
    {
        var configWithNullReviewer = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/testorg",
            "TestProject",
            null, // ReviewerId = null → should skip
            60,
            true,
            DateTimeOffset.UtcNow,
            []);

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        var sut = BuildSut(gitClient);
        var result = await sut.ListAssignedOpenReviewsAsync(configWithNullReviewer);

        Assert.Empty(result);
        // ADO was never called since we short-circuit on null ReviewerId
        await gitClient.DidNotReceiveWithAnyArgs()
            .GetPullRequestsByProjectAsync(
                null!,
                null!);
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_TakesMaxIterationId()
    {
        var repoId = Guid.NewGuid();

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequest> { MakePr(42, repoId) }));

        gitClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<GitPullRequestIteration>
                    {
                        new() { Id = 1 },
                        new() { Id = 5 },
                        new() { Id = 3 },
                    }));

        var sut = BuildSut(gitClient);
        var result = await sut.ListAssignedOpenReviewsAsync(DefaultConfig);

        Assert.Single(result);
        Assert.Equal(5, result[0].RevisionId);
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_TwoPrs_CallsGetIterationsForEach()
    {
        var repoId1 = Guid.NewGuid();
        var repoId2 = Guid.NewGuid();

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<GitPullRequest>
                    {
                        MakePr(10, repoId1),
                        MakePr(20, repoId2),
                    }));

        gitClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<GitPullRequestIteration>
                    {
                        new() { Id = 3 },
                    }));

        var sut = BuildSut(gitClient);
        var result = await sut.ListAssignedOpenReviewsAsync(DefaultConfig);

        Assert.Equal(2, result.Count);
        await gitClient.Received(2)
            .GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_UsesCriteriaWithCorrectReviewerIdAndActiveStatus()
    {
        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequest>()));

        var sut = BuildSut(gitClient);
        await sut.ListAssignedOpenReviewsAsync(DefaultConfig);

        await gitClient.Received(1)
            .GetPullRequestsByProjectAsync(
                Arg.Is(DefaultConfig.ProviderProjectKey),
                Arg.Is<GitPullRequestSearchCriteria>(c =>
                    c.ReviewerId == DefaultConfig.ReviewerId &&
                    c.Status == PullRequestStatus.Active),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_WithNullCredentials_LooksUpCredentials()
    {
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository
            .GetOperationalConnectionAsync(
                DefaultConfig.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());
        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequest>()));

        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var fetcher = new AdoAssignedPrFetcher(
            factory,
            connectionRepository,
            NullLogger<AdoAssignedPrFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);

        await fetcher.ListAssignedOpenReviewsAsync(DefaultConfig);

        // The provider-neutral connection lookup must still be queried even when nothing is configured.
        await connectionRepository.Received(1)
            .GetOperationalConnectionAsync(
                DefaultConfig.ClientId,
                Arg.Is<ProviderHostRef>(host =>
                    host.Provider == ScmProvider.AzureDevOps &&
                    host.HostBaseUrl == new ProviderHostRef(ScmProvider.AzureDevOps, DefaultConfig.ProviderScopePath)
                        .HostBaseUrl),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_WithPerClientCredentials_LooksUpCredentials()
    {
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository
            .GetOperationalConnectionAsync(
                DefaultConfig.ClientId,
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ClientScmConnectionCredentialDto?>(
                    new ClientScmConnectionCredentialDto(
                        Guid.NewGuid(),
                        DefaultConfig.ClientId,
                        ScmProvider.AzureDevOps,
                        DefaultConfig.ProviderScopePath,
                        ScmAuthenticationKind.OAuthClientCredentials,
                        "ado connection",
                        "secret",
                        true)
                    {
                        OAuthTenantId = "tenant",
                        OAuthClientId = "client",
                    }));

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());
        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequest>()));

        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var fetcher = new AdoAssignedPrFetcher(
            factory,
            connectionRepository,
            NullLogger<AdoAssignedPrFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);

        await fetcher.ListAssignedOpenReviewsAsync(DefaultConfig);

        await connectionRepository.Received(1)
            .GetOperationalConnectionAsync(
                DefaultConfig.ClientId,
                Arg.Is<ProviderHostRef>(host =>
                    host.Provider == ScmProvider.AzureDevOps &&
                    host.HostBaseUrl == new ProviderHostRef(ScmProvider.AzureDevOps, DefaultConfig.ProviderScopePath)
                        .HostBaseUrl),
                Arg.Any<CancellationToken>());
    }

    // T037 — repo filter tests

    private static GitPullRequest MakePrFull(int prId, string repoName, string targetRefName = "refs/heads/main")
    {
        return new GitPullRequest
        {
            PullRequestId = prId,
            Repository = new GitRepository { Id = Guid.NewGuid(), Name = repoName },
            TargetRefName = targetRefName,
        };
    }

    private static GitHttpClient BuildGitClientWithPrs(IReadOnlyList<GitPullRequest> prs)
    {
        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());
        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(prs.ToList()));
        gitClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequestIteration> { new() { Id = 1 } }));
        return gitClient;
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_EmptyFilterList_ReturnsAllPrs()
    {
        // T037: empty filter = backward compat, all PRs returned
        var config = DefaultConfig with { RepoFilters = [] };
        var prs = new[]
        {
            MakePrFull(1, "repo-a"),
            MakePrFull(2, "repo-b"),
        };
        var sut = BuildSut(BuildGitClientWithPrs(prs));

        var result = await sut.ListAssignedOpenReviewsAsync(config);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_RepoFilterExcludesNonMatchingRepo()
    {
        // T037: filter for "repo-a" only — "repo-b" should be excluded
        var filter = new CrawlRepoFilterDto(Guid.NewGuid(), "repo-a", []);
        var config = DefaultConfig with { RepoFilters = [filter] };
        var prs = new[]
        {
            MakePrFull(1, "repo-a"),
            MakePrFull(2, "repo-b"),
        };
        var sut = BuildSut(BuildGitClientWithPrs(prs));

        var result = await sut.ListAssignedOpenReviewsAsync(config);

        Assert.Single(result);
        Assert.Equal(1, result[0].CodeReview.Number);
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_BranchPatternFilterExcludesNonMatchingBranch()
    {
        // T037: filter for "repo-a" with target branch "main" only
        var filter = new CrawlRepoFilterDto(Guid.NewGuid(), "repo-a", ["main"]);
        var config = DefaultConfig with { RepoFilters = [filter] };
        var prs = new[]
        {
            MakePrFull(1, "repo-a"), // matches
            MakePrFull(2, "repo-a", "refs/heads/develop"), // excluded by branch filter
        };
        var sut = BuildSut(BuildGitClientWithPrs(prs));

        var result = await sut.ListAssignedOpenReviewsAsync(config);

        Assert.Single(result);
        Assert.Equal(1, result[0].CodeReview.Number);
    }

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_GlobBranchPattern_MatchesReleaseBranches()
    {
        // T037: glob "release/*" matches "release/2.1"
        var filter = new CrawlRepoFilterDto(Guid.NewGuid(), "repo-a", ["release/*"]);
        var config = DefaultConfig with { RepoFilters = [filter] };
        var prs = new[]
        {
            MakePrFull(1, "repo-a", "refs/heads/release/2.1"), // matches glob
            MakePrFull(2, "repo-a"), // does not match
        };
        var sut = BuildSut(BuildGitClientWithPrs(prs));

        var result = await sut.ListAssignedOpenReviewsAsync(config);

        Assert.Single(result);
        Assert.Equal(1, result[0].CodeReview.Number);
    }
}
