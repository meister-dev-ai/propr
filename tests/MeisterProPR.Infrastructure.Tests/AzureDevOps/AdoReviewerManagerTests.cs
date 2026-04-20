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

    private static AdoReviewerManager BuildSut(
        GitHttpClient gitClient,
        IClientScmConnectionRepository? connectionRepository = null,
        IClientScmScopeRepository? scopeRepository = null)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        connectionRepository ??= Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));
        scopeRepository ??= Substitute.For<IClientScmScopeRepository>();
        var mgr = new AdoReviewerManager(
            factory,
            connectionRepository,
            scopeRepository,
            NullLogger<AdoReviewerManager>.Instance);
        mgr.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        return mgr;
    }

    [Fact]
    public async Task AddOptionalReviewerAsync_ReviewOverload_UsesEnabledOrganizationScopeUrl()
    {
        var reviewerId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var gitClient = BuildGitClient();
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var scopeRepository = Substitute.For<IClientScmScopeRepository>();

        connectionRepository.GetOperationalConnectionAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    connectionId,
                    clientId,
                    ScmProvider.AzureDevOps,
                    "https://dev.azure.com",
                    ScmAuthenticationKind.OAuthClientCredentials,
                    "Azure DevOps",
                    true,
                    "verified",
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);
        scopeRepository.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmScopeDto(
                    Guid.NewGuid(),
                    clientId,
                    connectionId,
                    "organization",
                    "testorg",
                    OrgUrl,
                    "Test Org",
                    "verified",
                    true,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        gitClient.GetPullRequestReviewersAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<IdentityRefWithVote>()));
        gitClient.CreatePullRequestReviewerAsync(
                Arg.Any<IdentityRefWithVote>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IdentityRefWithVote()));

        var sut = BuildSut(gitClient, connectionRepository, scopeRepository);
        string? resolvedOrganizationUrl = null;
        sut.GitClientResolver = (organizationUrl, _) =>
        {
            resolvedOrganizationUrl = organizationUrl;
            return Task.FromResult(gitClient);
        };

        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com");
        var repository = new RepositoryRef(host, RepositoryId, ProjectId, ProjectId);
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, PrId.ToString(), PrId);
        var reviewer = new ReviewerIdentity(host, reviewerId.ToString(), "bot@testorg", "Bot", true);

        await sut.AddOptionalReviewerAsync(clientId, review, reviewer);

        Assert.Equal(OrgUrl, resolvedOrganizationUrl);
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

        gitClient.GetPullRequestReviewersAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<IdentityRefWithVote>()));

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

    [Fact]
    public async Task AddOptionalReviewerAsync_ReviewerAlreadyPresent_SkipsCreateCall()
    {
        var reviewerId = Guid.NewGuid();
        var gitClient = BuildGitClient();

        // Reviewer is already in the list returned by ADO
        var existing = new List<IdentityRefWithVote>
        {
            new() { Id = reviewerId.ToString() },
        };
        gitClient.GetPullRequestReviewersAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(existing));

        var sut = BuildSut(gitClient);

        await sut.AddOptionalReviewerAsync(OrgUrl, ProjectId, RepositoryId, PrId, reviewerId);

        // Create must NOT be called when reviewer is already on the PR
        await gitClient.DidNotReceive()
            .CreatePullRequestReviewerAsync(
                Arg.Any<IdentityRefWithVote>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOptionalReviewerAsync_ReviewerNotPresent_CallsCreate()
    {
        var reviewerId = Guid.NewGuid();
        var otherReviewerId = Guid.NewGuid();
        var gitClient = BuildGitClient();

        // List contains a different reviewer, not ours
        var existing = new List<IdentityRefWithVote>
        {
            new() { Id = otherReviewerId.ToString() },
        };
        gitClient.GetPullRequestReviewersAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(existing));

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
                Arg.Is<IdentityRefWithVote>(r => r.Id == reviewerId.ToString()),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }
}
