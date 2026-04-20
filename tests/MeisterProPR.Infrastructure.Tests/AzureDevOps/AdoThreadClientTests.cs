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
///     Unit tests for <see cref="AdoThreadClient" />.
///     A <see cref="GitHttpClient" /> substitute is injected via the internal resolver
///     to avoid a real ADO connection.
/// </summary>
public sealed class AdoThreadClientTests
{
    private static AdoThreadClient BuildSut(
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
        var sut = new AdoThreadClient(
            factory,
            connectionRepository,
            scopeRepository,
            NullLogger<AdoThreadClient>.Instance);
        sut.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        return sut;
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_ThreadOverload_UsesEnabledOrganizationScopeUrl()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var scopeRepository = Substitute.For<IClientScmScopeRepository>();
        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

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
                    "https://dev.azure.com/testorg",
                    "Test Org",
                    "verified",
                    true,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        gitClient.UpdateThreadAsync(
                Arg.Any<GitPullRequestCommentThread>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequestCommentThread()));

        var sut = BuildSut(gitClient, connectionRepository, scopeRepository);
        string? resolvedOrganizationUrl = null;
        sut.GitClientResolver = (organizationUrl, _) =>
        {
            resolvedOrganizationUrl = organizationUrl;
            return Task.FromResult(gitClient);
        };

        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com");
        var repository = new RepositoryRef(host, "repo-id", "TestProject", "TestProject");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "1", 1);
        var thread = new ReviewThreadRef(review, "99", "/src/Foo.cs", 1, true);

        await sut.UpdateThreadStatusAsync(clientId, thread, "fixed");

        Assert.Equal("https://dev.azure.com/testorg", resolvedOrganizationUrl);
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_CallsUpdatePullRequestThreadAsync()
    {
        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.UpdateThreadAsync(
                Arg.Any<GitPullRequestCommentThread>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequestCommentThread()));

        var sut = BuildSut(gitClient);

        await sut.UpdateThreadStatusAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            99,
            "fixed");

        await gitClient.Received(1)
            .UpdateThreadAsync(
                Arg.Is<GitPullRequestCommentThread>(t => t.Status == CommentThreadStatus.Fixed),
                Arg.Is("TestProject"),
                Arg.Is("repo-id"),
                Arg.Is(1),
                Arg.Is(99),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateThreadStatusAsync_UnknownStatus_UsesUnknownEnum()
    {
        var gitClient = Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());

        gitClient.UpdateThreadAsync(
                Arg.Any<GitPullRequestCommentThread>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitPullRequestCommentThread()));

        var sut = BuildSut(gitClient);

        // "unknown_status" does not parse to a known enum value → falls back to Unknown
        await sut.UpdateThreadStatusAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            5,
            "unknown_status");

        await gitClient.Received(1)
            .UpdateThreadAsync(
                Arg.Is<GitPullRequestCommentThread>(t => t.Status == CommentThreadStatus.Unknown),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>());
    }
}
