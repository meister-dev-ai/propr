// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoReviewerThreadStatusFetcherTests
{
    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo, false)]
    [InlineData(FindingBadgeStyle.ShieldsIo, true)]
    [InlineData(FindingBadgeStyle.Plain, true)]
    public async Task BadgeHeaderDoesNotDisplaceTheFindingOrRepliesFromBoundedMemoryHistory(FindingBadgeStyle style, bool longNames)
    {
        var botId = Guid.NewGuid();
        var finding = new ReviewComment("a.cs", 3, CommentSeverity.Error, "Leaks tenant data.")
        {
            RiskAssessment = new(
                FindingImpact.Critical, FindingLikelihood.Likely, "Tenant data is deleted.", "Every request uses this query.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path is visible."),
            FindingClassification = new(
                ["logic-error"], Enumerable.Range(0, 63)
                    .Select(index => new FindingCustomTypeAssignment(
                        Guid.NewGuid(), $"custom-{index}", longNames ? new string('x', 120) + $"{index:00}" : $"A normal custom finding type {index:00}"))
                    .ToList(),
                null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
        };
        var outbound = AdoFindingCommentRenderer.Format(finding, badgeStyle: style) + "\n\n*Configured publication marker*";
        Assert.True(outbound.IndexOf(finding.Message, StringComparison.Ordinal) > 8_000);
        var native = new GitPullRequestCommentThread
        {
            Id = 42, Status = CommentThreadStatus.Active,
            Comments =
            [
                CreateComment("Bot", botId, outbound, commentId: 1), CreateComment("Dev", Guid.NewGuid(), "I verified the affected tenant.", commentId: 2)
            ],
        };
        var git = MakeGitClient();
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns([native]);
        var snapshot = await BuildSut(git, botId).ReadSnapshotAsync(
            "https://dev.azure.com/org", "project", "repo", 1, ThreadOwnershipResolver.None, Guid.NewGuid());
        var history = Assert.Single(snapshot.ReviewerThreads).CommentHistory;
        Assert.Contains(finding.Message, history);
        Assert.Contains("Tenant data is deleted.", history);
        Assert.Contains("I verified the affected tenant.", history);
        Assert.Contains("types omitted", history);
        Assert.InRange(history.Length, 1, 8_000);
        Assert.DoesNotContain("https://img.shields.io", history);
        Assert.Equal(outbound, native.Comments[0].Content);
        Assert.Equal(outbound, Assert.Single(snapshot.Threads).Comments[0].Content);
        Assert.Equal("Leaks tenant data.", finding.Message);
    }

    [Theory]
    [InlineData(false, "RIGHT", "original-head")]
    [InlineData(true, "LEFT", "original-common")]
    public async Task ConcernAnchorKeepsOriginalTrackedPositionsAndTheCreationIteration(bool left, string side, string commit)
    {
        var git = MakeGitClient();
        var comment = CreateComment("human", Guid.NewGuid(), "Concern", commentId: 1);
        comment.PublishedDate = DateTime.Parse("2026-10-01T12:00:00Z").ToUniversalTime();
        var native = new GitPullRequestCommentThread
        {
            Id = 5, Status = CommentThreadStatus.Active, Comments = [comment],
            ThreadContext = new CommentThreadContext { FilePath = "renamed.cs", RightFileStart = new CommentPosition { Line = 99 } },
            PullRequestThreadContext = new GitPullRequestCommentThreadContext
            {
                IterationContext = new CommentIterationContext { FirstComparingIteration = 1, SecondComparingIteration = 1 },
                TrackingCriteria = new CommentTrackingCriteria
                {
                    FirstComparingIteration = 1, SecondComparingIteration = 2,
                    OrigFilePath = "original.cs", OrigLeftFileStart = left ? new CommentPosition { Line = 12 } : null,
                    OrigRightFileStart = left ? null : new CommentPosition { Line = 12 }
                }
            }
        };
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns([native]);
        git.GetPullRequestIterationsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new GitPullRequestIteration
                {
                    Id = 1, SourceRefCommit = new GitCommitRef { CommitId = "original-head" },
                    CommonRefCommit = new GitCommitRef { CommitId = "original-common" }
                },
                new GitPullRequestIteration { Id = 2, SourceRefCommit = new GitCommitRef { CommitId = "new-head" } }
            ]);
        var snapshot = await BuildSut(git, null).ReadSnapshotAsync(
            "https://dev.azure.com/org", "project", "repo", 1, ThreadOwnershipResolver.None, Guid.NewGuid());
        var anchor = Assert.Single(snapshot.Threads).ConcernAnchor;
        Assert.NotNull(anchor);
        Assert.True(anchor.IsKnown);
        Assert.Equal("original.cs", anchor.FilePath);
        Assert.Equal(12, anchor.StartLine);
        Assert.Equal(side, anchor.Side);
        Assert.Equal(commit, anchor.CommitSha);
    }

    [Fact]
    public async Task OrdinaryAndStrictAdoStatusesPropagateRequestedCancellation()
    {
        var git = MakeGitClient();
        using var cancellation = new CancellationTokenSource();
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<List<GitPullRequestCommentThread>>(cancellation.Token);
            });
        var sut = BuildSut(git, null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/org", "p", "r", 1, ThreadOwnershipResolver.None, Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetFeedbackStatusesAsync(
            "https://dev.azure.com/org", "p", "r", 1, ThreadOwnershipResolver.None, Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    public async Task HistoricalFeedbackPropagatesReadFailureWhileOrdinaryStatusRemainsBestEffort()
    {
        var sut = BuildSut(MakeGitClient(), null);
        sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(new InvalidOperationException("unavailable"));
        Assert.Empty(await sut.GetReviewerThreadStatusesAsync("https://dev.azure.com/org", "p", "r", 1, ThreadOwnershipResolver.None, Guid.NewGuid()));
        var strict = (IProviderReviewerThreadStatusFetcher)sut;
        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.GetFeedbackStatusesAsync(
            "https://dev.azure.com/org", "p", "r", 1,
            ThreadOwnershipResolver.None, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("05")]
    public async Task IdentifiedEventReadsOnlyTheRequestedThreadAfterInitialEnumeration(string id)
    {
        var git = MakeGitClient();
        var native = new GitPullRequestCommentThread
        {
            Id = 5, Status = CommentThreadStatus.Active,
            Comments = [CreateComment("Human", Guid.NewGuid(), "Edited concern", commentId: 2)]
        };
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns([native]);
        git.GetPullRequestThreadAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(native);
        var sut = BuildSut(git, null);
        await sut.ReadSnapshotAsync("https://dev.azure.com/org", "project", "repo", 1, ThreadOwnershipResolver.None, Guid.NewGuid());
        git.ClearReceivedCalls();
        var targeted = await sut.ReadTargetedSnapshotAsync(
            "https://dev.azure.com/org", "project", "repo", 1, [id, "5"], ThreadOwnershipResolver.None, Guid.NewGuid());
        Assert.False(targeted.EnumerationComplete);
        Assert.Equal(["5"], targeted.TargetThreadIds);
        Assert.Equal("5", Assert.Single(targeted.Threads).ThreadId);
        await git.Received(1).GetPullRequestThreadAsync(
            "project", "repo", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        await git.DidNotReceive().GetThreadsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

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
        bool isDeleted = false,
        short commentId = 0)
    {
        return new Comment
        {
            Id = commentId,
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
                    CreateComment("Bot", servicePrincipalId, "Please fix this.", commentId: 1),
                    CreateComment("Dev", developerId, "I think it's fine.", commentId: 2),
                    CreateComment("Bot", servicePrincipalId, "Can you clarify?", commentId: 3),
                    CreateComment("System", servicePrincipalId, "Auto-status", CommentType.System, commentId: 4),
                },
            },
            new()
            {
                Id = 99,
                Status = CommentThreadStatus.Active,
                ThreadContext = new CommentThreadContext { FilePath = "/src/Bar.cs" },
                Comments = new List<Comment>
                {
                    CreateComment("Human", otherAuthorId, "Unrelated thread.", commentId: 1),
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
            ThreadOwnershipResolver.None,
            Guid.NewGuid(),
            CancellationToken.None);

        var entry = Assert.Single(result);
        Assert.Equal("42", entry.ThreadId);
        Assert.Equal("Active", entry.Status);
        Assert.Equal("/src/Foo.cs", entry.FilePath);
        Assert.Equal(1, entry.NonReviewerReplyCount);
        Assert.Contains("Bot: Please fix this.", entry.CommentHistory);
        Assert.Contains("Dev: I think it's fine.", entry.CommentHistory);
        Assert.Contains("Bot: Can you clarify?", entry.CommentHistory);
        Assert.DoesNotContain("Auto-status", entry.CommentHistory);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ProvenanceRecordsTheThread_IncludesItWhateverAccountPostedIt()
    {
        // Azure DevOps scopes a comment id to its thread, so the pair identifies the origin. The account is
        // one the connection does not recognise, and the thread is still ProPR's because ProPR recorded
        // posting it.
        var foreignAccountId = Guid.NewGuid();
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
                            Id = 42,
                            Status = CommentThreadStatus.Active,
                            Comments = new List<Comment>
                            {
                                CreateComment("Retired Bot Account", foreignAccountId, "Please fix this.", commentId: 1),
                            },
                        },
                    }));

        var sut = BuildSut(gitClient, Guid.NewGuid());

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            ThreadOwnershipResolver.Create(
                [new PostedCommentOriginRow("42", "1", Guid.NewGuid())],
                ThreadOwnerIdentity.None,
                ProviderCommentIdScope.Thread),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal("42", Assert.Single(result).ThreadId);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_HumanThreadWhoseFirstCommentSharesARecordedNumber_IsExcluded()
    {
        // Azure DevOps numbers a comment within its thread, so the first comment of every thread on the pull
        // request is comment 1. A review that posted only a summary leaves exactly one provenance row, at
        // comment id 1. Resolving that row on the comment id alone would make every human-raised thread here
        // ProPR's: each evaluated at the cost of a model call, and under the reply-on-resolve behaviour
        // answered and closed in someone else's conversation.
        var summaryJobId = Guid.NewGuid();
        var retiredBotAccountId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

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
                            Id = 17,
                            Status = CommentThreadStatus.Active,
                            Comments = new List<Comment>
                            {
                                CreateComment("Review Bot", retiredBotAccountId, "**AI Review Summary**", commentId: 1),
                            },
                        },
                        new()
                        {
                            Id = 18,
                            Status = CommentThreadStatus.Active,
                            ThreadContext = new CommentThreadContext { FilePath = "/src/Foo.cs" },
                            Comments = new List<Comment>
                            {
                                CreateComment("Jane Dev", humanId, "This looks wrong to me.", commentId: 1),
                            },
                        },
                    }));

        var sut = BuildSut(gitClient, Guid.NewGuid());

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            ThreadOwnershipResolver.Create(
                [new PostedCommentOriginRow("17", "1", summaryJobId)],
                ThreadOwnerIdentity.None,
                ProviderCommentIdScope.Thread),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal("17", Assert.Single(result).ThreadId);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ThreadAuthoredByTheConfiguredReviewer_IsExcluded()
    {
        // The deliberate narrowing. A client whose configured reviewer differs from the account its token
        // authenticates as used to have that reviewer's threads counted as ProPR's. Ownership now rests on
        // provenance and the token identity alone, and the configured reviewer is neither.
        var configuredReviewerId = Guid.NewGuid();
        var tokenIdentityId = Guid.NewGuid();

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
                                CreateComment("Configured Reviewer", configuredReviewerId, "Please fix this.", commentId: 1),
                            },
                        },
                    }));

        var sut = BuildSut(gitClient, tokenIdentityId);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            ThreadOwnershipResolver.None,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ResolvedThread_ReportsCodeChangeFromIterationDiff()
    {
        var botId = Guid.NewGuid();

        var gitClient = MakeGitClient();

        GitPullRequestCommentThread ResolvedThread(int id, string filePath)
        {
            return new GitPullRequestCommentThread
            {
                Id = id,
                Status = CommentThreadStatus.Fixed,
                ThreadContext = new CommentThreadContext { FilePath = filePath },
                PullRequestThreadContext = new GitPullRequestCommentThreadContext
                {
                    IterationContext = new CommentIterationContext
                    {
                        FirstComparingIteration = 1,
                        SecondComparingIteration = 1,
                    },
                },
                Comments = new List<Comment> { CreateComment("Bot", botId, "Please fix this.", commentId: 1) },
            };
        }

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
                        ResolvedThread(42, "/src/Changed.cs"),
                        ResolvedThread(43, "/src/Untouched.cs"),
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
                        new() { Id = 1 },
                        new() { Id = 2 },
                    }));

        gitClient.GetPullRequestIterationChangesAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new GitPullRequestIterationChanges
                    {
                        ChangeEntries = new List<GitPullRequestChange>
                        {
                            new() { Item = new GitItem { Path = "/src/Changed.cs" } },
                        },
                    }));

        var sut = BuildSut(gitClient, botId);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            ThreadOwnershipResolver.None,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(
            ThreadAnchorCodeChange.Changed,
            result.Single(entry => entry.ThreadId == "42").CodeChangedSinceRaised);
        Assert.Equal(
            ThreadAnchorCodeChange.Unchanged,
            result.Single(entry => entry.ThreadId == "43").CodeChangedSinceRaised);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_WithoutAuthorizedIdentity_ExcludesServicePrincipalOwnedThreads()
    {
        // Nothing to decide with: the connection resolved no identity and no provenance row covers the
        // thread. It stays out, and under the narrowing no configured reviewer can bring it back in.
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
                                CreateComment("Bot", servicePrincipalId, "Please fix this.", commentId: 1),
                            },
                        },
                    }));

        var sut = BuildSut(gitClient, null);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://dev.azure.com/testorg",
            "TestProject",
            "repo-id",
            1,
            ThreadOwnershipResolver.None,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Empty(result);
    }
}
