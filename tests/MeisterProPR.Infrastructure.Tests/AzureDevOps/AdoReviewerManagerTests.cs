using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Unit tests for <see cref="AdoReviewerManager" />.
///     A <see cref="GitHttpClient" /> substitute is injected via
///     <see cref="AdoReviewerManager.GitClientResolver" /> to avoid a real ADO connection.
/// </summary>
public sealed class AdoReviewerManagerTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string ProjectId = "TestProject";
    private const string RepositoryId = "repo-id";
    private const int PrId = 42;

    private static GitHttpClient BuildGitClient()
    {
        return Substitute.For<GitHttpClient>(
            new Uri(OrgUrl),
            new VssCredentials());
    }

    private static AdoReviewerManager BuildSut(GitHttpClient gitClient)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var credRepo = Substitute.For<IClientAdoCredentialRepository>();
        credRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(null));
        var mgr = new AdoReviewerManager(factory, credRepo, NullLogger<AdoReviewerManager>.Instance);
        mgr.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        return mgr;
    }

    [Fact]
    public async Task AddOptionalReviewerAsync_ADoThrows_PropagatesException()
    {
        var reviewerId = Guid.NewGuid();
        var gitClient = BuildGitClient();

        gitClient.CreatePullRequestReviewerAsync(
                Arg.Any<IdentityRefWithVote>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Permission denied"));

        var sut = BuildSut(gitClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AddOptionalReviewerAsync(OrgUrl, ProjectId, RepositoryId, PrId, reviewerId));
    }

    // T028 — idempotency: calling twice does not throw

    [Fact]
    public async Task AddOptionalReviewerAsync_CalledTwice_DoesNotThrow()
    {
        var reviewerId = Guid.NewGuid();
        var gitClient = BuildGitClient();

        gitClient.CreatePullRequestReviewerAsync(
                Arg.Any<IdentityRefWithVote>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IdentityRefWithVote()));

        var sut = BuildSut(gitClient);

        // ADO PUT /reviewers is idempotent — no exception expected on second call
        await sut.AddOptionalReviewerAsync(OrgUrl, ProjectId, RepositoryId, PrId, reviewerId);
        await sut.AddOptionalReviewerAsync(OrgUrl, ProjectId, RepositoryId, PrId, reviewerId);

        await gitClient.Received(2)
            .CreatePullRequestReviewerAsync(
                Arg.Any<IdentityRefWithVote>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    // T027 — CreatePullRequestReviewerAsync called with Vote=0, IsRequired=false

    [Fact]
    public async Task AddOptionalReviewerAsync_CallsCreateWithVote0AndNotRequired()
    {
        var reviewerId = Guid.NewGuid();
        var gitClient = BuildGitClient();

        gitClient.CreatePullRequestReviewerAsync(
                Arg.Any<IdentityRefWithVote>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IdentityRefWithVote()));

        var sut = BuildSut(gitClient);

        await sut.AddOptionalReviewerAsync(OrgUrl, ProjectId, RepositoryId, PrId, reviewerId);

        await gitClient.Received(1)
            .CreatePullRequestReviewerAsync(
                Arg.Is<IdentityRefWithVote>(r =>
                    r.Vote == 0 &&
                    r.IsRequired == false &&
                    r.Id == reviewerId.ToString()),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }
}
