using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>Unit tests for <see cref="AdoPrStatusFetcher" />.</summary>
public sealed class AdoPrStatusFetcherTests
{
    private static AdoPrStatusFetcher BuildSut(GitHttpClient gitClient)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var credRepo = Substitute.For<IClientAdoCredentialRepository>();
        credRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(null));
        var fetcher = new AdoPrStatusFetcher(factory, credRepo, NullLogger<AdoPrStatusFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        return fetcher;
    }

    private static GitHttpClient MakeGitClient() =>
        Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

    [Fact]
    public async Task GetStatusAsync_ActiveAdoStatus_ReturnsPrStatusActive()
    {
        // Arrange
        var gitClient = MakeGitClient();
        gitClient.GetPullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequest { Status = PullRequestStatus.Active }));

        var sut = BuildSut(gitClient);

        // Act
        var result = await sut.GetStatusAsync("https://dev.azure.com/org", "proj", "repo", 42, null);

        // Assert
        Assert.Equal(PrStatus.Active, result);
    }

    [Fact]
    public async Task GetStatusAsync_AbandonedAdoStatus_ReturnsPrStatusAbandoned()
    {
        // Arrange
        var gitClient = MakeGitClient();
        gitClient.GetPullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequest { Status = PullRequestStatus.Abandoned }));

        var sut = BuildSut(gitClient);

        // Act
        var result = await sut.GetStatusAsync("https://dev.azure.com/org", "proj", "repo", 99, null);

        // Assert
        Assert.Equal(PrStatus.Abandoned, result);
    }

    [Fact]
    public async Task GetStatusAsync_CompletedAdoStatus_ReturnsPrStatusCompleted()
    {
        // Arrange
        var gitClient = MakeGitClient();
        gitClient.GetPullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequest { Status = PullRequestStatus.Completed }));

        var sut = BuildSut(gitClient);

        // Act
        var result = await sut.GetStatusAsync("https://dev.azure.com/org", "proj", "repo", 77, null);

        // Assert
        Assert.Equal(PrStatus.Completed, result);
    }

    [Fact]
    public async Task GetStatusAsync_GitClientThrows_ReturnsActiveFallback()
    {
        // Arrange: fail-safe contract — any exception must be swallowed and Active returned
        var gitClient = MakeGitClient();
        gitClient.GetPullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns<GitPullRequest>(_ => throw new Exception("ADO rate limit exceeded"));

        var sut = BuildSut(gitClient);

        // Act — must NOT rethrow
        var result = await sut.GetStatusAsync("https://dev.azure.com/org", "proj", "repo", 11, null);

        // Assert: fail-safe default
        Assert.Equal(PrStatus.Active, result);
    }

    [Fact]
    public async Task GetStatusAsync_NullStatusInResponse_ReturnsPrStatusActive()
    {
        // Arrange: ADO SDK may return null/NotSet for edge cases
        var gitClient = MakeGitClient();
        gitClient.GetPullRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequest { Status = PullRequestStatus.NotSet }));

        var sut = BuildSut(gitClient);

        // Act
        var result = await sut.GetStatusAsync("https://dev.azure.com/org", "proj", "repo", 5, null);

        // Assert: NotSet maps to Active (safe default)
        Assert.Equal(PrStatus.Active, result);
    }
}
