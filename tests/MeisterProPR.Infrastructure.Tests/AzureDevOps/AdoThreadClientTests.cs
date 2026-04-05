// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
///     Unit tests for <see cref="AdoThreadClient" />.
///     A <see cref="GitHttpClient" /> substitute is injected via the internal resolver
///     to avoid a real ADO connection.
/// </summary>
public sealed class AdoThreadClientTests
{
    private static AdoThreadClient BuildSut(GitHttpClient gitClient)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var credRepo = Substitute.For<IClientAdoCredentialRepository>();
        credRepo.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(null));
        var sut = new AdoThreadClient(factory, credRepo, NullLogger<AdoThreadClient>.Instance);
        sut.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        return sut;
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
