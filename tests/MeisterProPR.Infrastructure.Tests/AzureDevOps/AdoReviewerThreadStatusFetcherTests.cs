// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoReviewerThreadStatusFetcherTests
{
    private static AdoReviewerThreadStatusFetcher BuildSut(GitHttpClient gitClient, Guid? authorizedIdentityId)
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProviderHostRef>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientScmConnectionCredentialDto?>(null));

        var fetcher = new AdoReviewerThreadStatusFetcher(
            factory,
            connectionRepository,
            NullLogger<AdoReviewerThreadStatusFetcher>.Instance);
        fetcher.GitClientResolver = (_, _) => Task.FromResult(gitClient);
        fetcher.AuthorizedIdentityResolver = (_, _) => Task.FromResult(authorizedIdentityId);
        return fetcher;
    }

    private static GitHttpClient MakeGitClient()
    {
        return Substitute.For<GitHttpClient>(
            new Uri("https://dev.azure.com/testorg"),
            new VssCredentials());
    }

    private static Comment CreateComment(
        string authorName,
        Guid authorId,
        string content,
        CommentType commentType = CommentType.Text,
        bool isDeleted = false)
    {
        return new Comment
        {
            Author = new IdentityRef
            {
                Id = authorId.ToString(),
                DisplayName = authorName,
            },
            Content = content,
            CommentType = commentType,
            IsDeleted = isDeleted,
        };
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_IncludesAuthorizedIdentityOwnedThreads()
    {
        var reviewerId = Guid.NewGuid();
        var servicePrincipalId = Guid.NewGuid();
        var developerId = Guid.NewGuid();
        var otherAuthorId = Guid.NewGuid();

        var gitClient = MakeGitClient();
        var threads = new List<GitPullRequestCommentThread>
        {
            new()
            {
                Id = 42,
                Status = CommentThreadStatus.Active,
                ThreadContext = new CommentThreadContext { FilePath = "/src/Foo.cs" },
                Comments = new List<Comment>
                {
                    CreateComment("Bot", servicePrincipalId, "Please fix this."),
                    CreateComment("Dev", developerId, "I think it's fine."),
                    CreateComment("Bot", servicePrincipalId, "Can you clarify?"),
                    CreateComment("System", reviewerId, "Auto-status", CommentType.System),
                },
            },
            new()
            {
                Id = 99,
                Status = CommentThreadStatus.Active,
                ThreadContext = new CommentThreadContext { FilePath = "/src/Bar.cs" },
                Comments = new List<Comment>
                {
                    CreateComment("Human", otherAuthorId, "Unrelated thread."),
                },
            },
        };

        gitClient.GetThreadsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(threads));

        var sut = BuildSut(gitClient, servicePrincipalId);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            reviewerId,
            Guid.NewGuid(),
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal(42, entry.ThreadId);
        Assert.Equal("Active", entry.Status);
        Assert.Equal("/src/Foo.cs", entry.FilePath);
        Assert.Equal(1, entry.NonReviewerReplyCount);
        Assert.Contains("Bot: Please fix this.", entry.CommentHistory);
        Assert.Contains("Dev: I think it's fine.", entry.CommentHistory);
        Assert.Contains("Bot: Can you clarify?", entry.CommentHistory);
        Assert.DoesNotContain("Auto-status", entry.CommentHistory);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_WithoutAuthorizedIdentity_ExcludesServicePrincipalOwnedThreads()
    {
        var reviewerId = Guid.NewGuid();
        var servicePrincipalId = Guid.NewGuid();

        var gitClient = MakeGitClient();
        gitClient.GetThreadsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<GitPullRequestCommentThread>
                    {
                        new()
                        {
                            Id = 7,
                            Status = CommentThreadStatus.Active,
                            Comments = new List<Comment>
                            {
                                CreateComment("Bot", servicePrincipalId, "Please fix this."),
                            },
                        },
                    }));

        var sut = BuildSut(gitClient, null);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            reviewerId,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Empty(result);
    }
}
