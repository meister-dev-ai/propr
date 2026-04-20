// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Providers.Common;

public sealed class ProviderPullRequestFetcherTests
{
    [Fact]
    public async Task FetchAsync_MatchedGitLabConnection_DelegatesToGitLabFetcher()
    {
        var clientId = Guid.NewGuid();
        var gitLabFetcher = Substitute.For<IProviderPullRequestFetcher>();
        gitLabFetcher.Provider.Returns(ScmProvider.GitLab);

        var azureFetcher = Substitute.For<IProviderPullRequestFetcher>();
        azureFetcher.Provider.Returns(ScmProvider.AzureDevOps);

        var expected = new PullRequest(
            "https://gitlab.example.com",
            "acme/platform",
            "101",
            "propr",
            42,
            1,
            "Provider adapters",
            null,
            "feature/providers",
            "main",
            []);

        gitLabFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(expected);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitLab,
                    "https://gitlab.example.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitLab",
                    true,
                    "verified",
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var sut = new ProviderPullRequestFetcher([azureFetcher, gitLabFetcher], connectionRepository);

        var result = await sut.FetchAsync(
            "https://gitlab.example.com/acme/platform",
            "acme/platform",
            "101",
            42,
            1,
            clientId: clientId,
            cancellationToken: CancellationToken.None);

        Assert.Same(expected, result);
        await gitLabFetcher.Received(1)
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                clientId,
                Arg.Any<CancellationToken>());
        await azureFetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default!, default!, default, default);
    }
}

public sealed class ProviderReviewContextToolsFactoryTests
{
    [Fact]
    public void Create_GitHubReview_DelegatesToGitHubFactory()
    {
        var gitHubFactory = Substitute.For<IProviderReviewContextToolsFactory>();
        gitHubFactory.Provider.Returns(ScmProvider.GitHub);
        var expectedTools = Substitute.For<IReviewContextTools>();
        gitHubFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(expectedTools);

        var gitLabFactory = Substitute.For<IProviderReviewContextToolsFactory>();
        gitLabFactory.Provider.Returns(ScmProvider.GitLab);

        var sut = new ProviderReviewContextToolsFactory([gitLabFactory, gitHubFactory]);
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var request = new ReviewContextToolsRequest(
            review,
            "feature/providers",
            7,
            Guid.NewGuid(),
            null,
            host.HostBaseUrl);

        var result = sut.Create(request);

        Assert.Same(expectedTools, result);
        gitHubFactory.Received(1).Create(request);
        gitLabFactory.DidNotReceiveWithAnyArgs().Create(default!);
    }
}

public sealed class ProviderReviewerThreadStatusFetcherTests
{
    [Fact]
    public async Task GetReviewerThreadStatusesAsync_MatchedGitHubConnection_DelegatesToGitHubProvider()
    {
        var clientId = Guid.NewGuid();
        var gitHubFetcher = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        gitHubFetcher.Provider.Returns(ScmProvider.GitHub);
        gitHubFetcher.GetReviewerThreadStatusesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new PrThreadStatusEntry(17, "Active", "src/Fetcher.cs", "meister-dev: Please revisit this.", 1),
            ]);

        var azureFetcher = Substitute.For<IProviderReviewerThreadStatusFetcher>();
        azureFetcher.Provider.Returns(ScmProvider.AzureDevOps);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    "https://github.example.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    true,
                    "verified",
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        var sut = new ProviderReviewerThreadStatusFetcher([azureFetcher, gitHubFetcher], connectionRepository);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://github.example.com/acme/propr",
            "acme",
            "101",
            42,
            Guid.NewGuid(),
            clientId,
            CancellationToken.None);

        Assert.Single(result);
        await gitHubFetcher.Received(1)
            .GetReviewerThreadStatusesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                42,
                Arg.Any<Guid>(),
                clientId,
                Arg.Any<CancellationToken>());
        await azureFetcher.DidNotReceiveWithAnyArgs()
            .GetReviewerThreadStatusesAsync(default!, default!, default!, default, default, default);
    }
}
