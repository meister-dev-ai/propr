using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.AzureDevOps;
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
        "https://dev.azure.com/testorg",
        "TestProject",
        Guid.NewGuid(),
        60,
        true,
        DateTimeOffset.UtcNow);

    private static AdoAssignedPrFetcher BuildSut(GitHttpClient gitClient)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var credRepo = Substitute.For<IClientAdoCredentialRepository>();
        credRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(null));
        var fetcher = new AdoAssignedPrFetcher(factory, credRepo, NullLogger<AdoAssignedPrFetcher>.Instance);
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
        var result = await sut.GetAssignedOpenPullRequestsAsync(DefaultConfig);

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
        var result = await sut.GetAssignedOpenPullRequestsAsync(DefaultConfig);

        // Only the second PR is included; first is skipped due to exception
        Assert.Single(result);
        Assert.Equal(22, result[0].PullRequestId);
    }

    // T035 — config with null ReviewerId is skipped and returns empty list

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_NullReviewerId_ReturnsEmptyListWithoutCallingAdo()
    {
        var configWithNullReviewer = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/testorg",
            "TestProject",
            null, // ReviewerId = null → should skip
            60,
            true,
            DateTimeOffset.UtcNow);

        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        var sut = BuildSut(gitClient);
        var result = await sut.GetAssignedOpenPullRequestsAsync(configWithNullReviewer);

        Assert.Empty(result);
        // ADO was never called since we short-circuit on null ReviewerId
        await gitClient.DidNotReceiveWithAnyArgs()
            .GetPullRequestsByProjectAsync(
                default(string)!,
                default!);
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
        var result = await sut.GetAssignedOpenPullRequestsAsync(DefaultConfig);

        Assert.Single(result);
        Assert.Equal(5, result[0].LatestIterationId);
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
        var result = await sut.GetAssignedOpenPullRequestsAsync(DefaultConfig);

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
        await sut.GetAssignedOpenPullRequestsAsync(DefaultConfig);

        await gitClient.Received(1)
            .GetPullRequestsByProjectAsync(
                Arg.Is(DefaultConfig.ProjectId),
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
        var credentialRepository = Substitute.For<IClientAdoCredentialRepository>();
        credentialRepository
            .GetByClientIdAsync(DefaultConfig.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(null));

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
        var fetcher = new AdoAssignedPrFetcher(factory, credentialRepository, NullLogger<AdoAssignedPrFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);

        await fetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig);

        // Credential repository must still be queried even when result is null
        await credentialRepository.Received(1)
            .GetByClientIdAsync(DefaultConfig.ClientId, Arg.Any<CancellationToken>());
    }

    // ── Per-client credential tests (T019) ───────────────────────────────────

    [Fact]
    public async Task GetAssignedOpenPullRequestsAsync_WithPerClientCredentials_LooksUpCredentials()
    {
        var credentialRepository = Substitute.For<IClientAdoCredentialRepository>();
        credentialRepository
            .GetByClientIdAsync(DefaultConfig.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(new ClientAdoCredentials("tenant", "client", "secret")));

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
        var fetcher = new AdoAssignedPrFetcher(factory, credentialRepository, NullLogger<AdoAssignedPrFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);

        await fetcher.GetAssignedOpenPullRequestsAsync(DefaultConfig);

        // Credential lookup for the config's ClientId must have been called
        await credentialRepository.Received(1)
            .GetByClientIdAsync(DefaultConfig.ClientId, Arg.Any<CancellationToken>());
    }
}