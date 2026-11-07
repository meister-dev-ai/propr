// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Services.Common;
using Azure.Core;
using NSubstitute;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Tests for AdoPullRequestFetcher mapping logic.
///     Since GitHttpClient is sealed, these tests verify the domain mapping helpers
///     used by the fetcher and overall integration shape.
///     Full integration tests against a real ADO instance are out of scope for CI.
/// </summary>
public class AdoPrFetcherTests
{
    [Fact]
    public async Task StrictFeedbackRejectsNullPayloadWhileOrdinaryThreadContextRemainsBestEffort()
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://dev.azure.com/testorg"), new VssCredentials());
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<GitPullRequestCommentThread>>(null!));
        var sut = new AdoPrFetcher(
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            Substitute.For<IClientScmConnectionRepository>(), NullLogger<AdoPrFetcher>.Instance)
        {
            GitClientResolver = (_, _) => Task.FromResult(git),
        };

        await Assert.ThrowsAsync<CodeInsightIncompleteEnumerationException>(() => sut.FetchFeedbackThreadsAsync(
            "https://dev.azure.com/testorg", "p", "r", 1, Guid.NewGuid()));
        Assert.Empty(await sut.FetchThreadsAsync("https://dev.azure.com/testorg", "p", "r", 1, Guid.NewGuid()));
    }

    [Fact]
    public async Task OrdinaryAndStrictAdoThreadReadsPropagateRequestedCancellation()
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://dev.azure.com/testorg"), new VssCredentials());
        using var cancellation = new CancellationTokenSource();
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<List<GitPullRequestCommentThread>>(cancellation.Token);
            });
        var sut = new AdoPrFetcher(
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            Substitute.For<IClientScmConnectionRepository>(), NullLogger<AdoPrFetcher>.Instance) { GitClientResolver = (_, _) => Task.FromResult(git) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.FetchThreadsAsync(
            "https://dev.azure.com/testorg", "p", "r", 1, Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.FetchFeedbackThreadsAsync(
            "https://dev.azure.com/testorg", "p", "r", 1, Guid.NewGuid(), cancellation.Token));
    }

    // A file whose review failed in the baseline joins the delta review. The iteration comparison does not
    // contain it when it did not change since the compared iteration, so the fetcher takes it from the change
    // listing of the whole pull request and reads its content like a file of the delta. A requested file that
    // the pull request no longer contains adds nothing.
    [Fact]
    public async Task FetchAsync_DeltaReview_AddsTheRequestedFilesFromTheFullListing()
    {
        var git = Substitute.For<GitHttpClient>(new Uri("https://dev.azure.com/testorg"), new VssCredentials());
        git.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(
                new GitPullRequest
                {
                    PullRequestId = 42,
                    Title = "Add fetchers",
                    SourceRefName = "refs/heads/feature",
                    TargetRefName = "refs/heads/main",
                    Repository = new GitRepository { Name = "repo" },
                });
        git.GetPullRequestIterationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(
                new GitPullRequestIteration
                {
                    SourceRefCommit = new GitCommitRef { CommitId = "head-sha" },
                    CommonRefCommit = new GitCommitRef { CommitId = "base-sha" },
                });

        // The comparison with iteration 2 lists the changed file, the listing without a comparison every file.
        git.GetPullRequestIterationChangesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                new GitPullRequestIterationChanges
                {
                    ChangeEntries = call.ArgAt<int?>(6) is null
                        ? new List<GitPullRequestChange> { EditOf("/src/Fetcher.cs"), EditOf("/src/Stable.cs"), EditOf("/src/Other.cs") }
                        : new List<GitPullRequestChange> { EditOf("/src/Fetcher.cs") },
                }));
        git.GetThreadsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(new List<GitPullRequestCommentThread>());

        var workspace = Substitute.For<IReviewRepositoryWorkspace>();
        workspace.ReadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => $"{call.ArgAt<string>(1)} {call.ArgAt<string>(0)}");
        workspace.GetUnifiedDiffAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => $"diff {call.ArgAt<string>(0)}");

        var sut = new AdoPrFetcher(
            new VssConnectionFactory(Substitute.For<TokenCredential>()),
            Substitute.For<IClientScmConnectionRepository>(), NullLogger<AdoPrFetcher>.Instance)
        {
            GitClientResolver = (_, _) => Task.FromResult(git),
        };

        var result = await sut.FetchAsync(
            "https://dev.azure.com/testorg",
            "project",
            "repo",
            42,
            3,
            compareToIterationId: 2,
            clientId: Guid.NewGuid(),
            workspace: workspace,
            additionalFilePaths: ["src/Stable.cs", "src/Removed.cs", "src/Fetcher.cs"]);

        Assert.Collection(
            result.ChangedFiles,
            item => Assert.Equal("src/Fetcher.cs", item.Path),
            item =>
            {
                Assert.Equal("src/Stable.cs", item.Path);
                Assert.Equal(ChangeType.Edit, item.ChangeType);
                Assert.Equal("source /src/Stable.cs", item.FullContent);
                Assert.Equal("diff /src/Stable.cs", item.UnifiedDiff);
            });
        Assert.Equal(
            ["src/Fetcher.cs", "src/Stable.cs", "src/Other.cs"],
            result.AllPrFileSummaries.Select(summary => summary.Path));
    }

    private static GitPullRequestChange EditOf(string path)
    {
        return new GitPullRequestChange { ChangeType = VersionControlChangeType.Edit, Item = new GitItem { Path = path } };
    }

    private static readonly ProviderHostRef AdoHost =
        new(ScmProvider.AzureDevOps, "https://dev.azure.com/acme");

    [Fact]
    public void ChangedFile_MapFromAdd_HasCorrectChangeType()
    {
        var file = new ChangedFile("/src/NewFile.cs", ChangeType.Add, "new content", "");
        Assert.Equal(ChangeType.Add, file.ChangeType);
        Assert.Equal("/src/NewFile.cs", file.Path);
    }

    [Fact]
    public void ChangedFile_MapFromDelete_HasEmptyFullContent()
    {
        var file = new ChangedFile("/src/Deleted.cs", ChangeType.Delete, "", "- deleted content");
        Assert.Equal(ChangeType.Delete, file.ChangeType);
        Assert.Empty(file.FullContent);
        Assert.NotEmpty(file.UnifiedDiff);
    }

    [Fact]
    public void ChangedFile_MapFromEdit_HasCorrectChangeType()
    {
        var file = new ChangedFile("/src/Existing.cs", ChangeType.Edit, "updated content", "- old\n+ new");
        Assert.Equal(ChangeType.Edit, file.ChangeType);
        Assert.NotEmpty(file.UnifiedDiff);
    }

    [Theory]
    [InlineData(ChangeType.Add, "new content", "")]
    [InlineData(ChangeType.Edit, "updated content", "- old\n+ new")]
    [InlineData(ChangeType.Delete, "", "- removed")]
    public void ChangedFile_VariousChangeTypes_CorrectlyConstructed(ChangeType changeType, string content, string diff)
    {
        var file = new ChangedFile("/file.cs", changeType, content, diff);
        Assert.Equal(changeType, file.ChangeType);
        Assert.Equal(content, file.FullContent);
        Assert.Equal(diff, file.UnifiedDiff);
    }

    [Theory]
    [InlineData("/src/Foo.cs", "src/Foo.cs")]
    [InlineData("/frontend/src/App.vue", "frontend/src/App.vue")]
    [InlineData("src/Already.cs", "src/Already.cs")]
    public void CreateSummaryFromChange_NormalizesAdoLeadingSlashToRepoRelativePath(string adoPath, string expected)
    {
        // Azure DevOps returns repo-root-absolute item paths (leading slash); the adapter must emit
        // repo-relative paths so downstream anchoring, scope, and retention keys match the other providers.
        var change = new GitPullRequestChange
        {
            Item = new GitItem { Path = adoPath },
            ChangeType = VersionControlChangeType.Edit,
        };

        var summary = AdoPrFetcher.CreateSummaryFromChange(change);

        Assert.NotNull(summary);
        Assert.Equal(expected, summary!.Path);
    }

    [Fact]
    public void ToPullRequestAuthor_MapsCreatedByToHostScopedIdentity()
    {
        var createdBy = new IdentityRef
        {
            Id = "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8",
            DisplayName = "Octo Dev",
            UniqueName = "octo.dev@acme.example",
        };

        var author = AdoPrFetcher.ToPullRequestAuthor(AdoHost, createdBy);

        Assert.NotNull(author);
        Assert.Equal(ScmProvider.AzureDevOps, author!.Host.Provider);
        Assert.Equal("https://dev.azure.com", author.Host.HostBaseUrl);
        Assert.Equal("6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8", author.ExternalUserId);
        Assert.Equal("octo.dev@acme.example", author.Login);
        Assert.Equal("Octo Dev", author.DisplayName);

        // Azure DevOps states nothing about a bot on the pull-request payload.
        Assert.Null(author.IsBot);
    }

    [Fact]
    public void ToPullRequestAuthor_WithoutUniqueName_UsesDisplayNameAsLogin()
    {
        var createdBy = new IdentityRef
        {
            Id = "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8",
            DisplayName = "Octo Dev",
        };

        var author = AdoPrFetcher.ToPullRequestAuthor(AdoHost, createdBy);

        Assert.Equal("Octo Dev", author!.Login);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToPullRequestAuthor_WithoutIdentityId_ReturnsNoAuthor(string? identityId)
    {
        var createdBy = identityId is null
            ? null
            : new IdentityRef { Id = identityId, DisplayName = "Octo Dev" };

        Assert.Null(AdoPrFetcher.ToPullRequestAuthor(AdoHost, createdBy));
    }

    // The identity GUID the comments API returns is the same one the pull-request payload carries for the
    // account, so an answered mention and a reviewed pull request name one person the same way.
    [Fact]
    public void ToThreadComment_CarriesTheIdentityGuidTheCommentsApiReturned()
    {
        var comment = new Comment
        {
            Id = 501,
            Content = "Please handle null.",
            Author = new IdentityRef
            {
                Id = "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8",
                DisplayName = "Octo Dev",
            },
        };

        var mapped = AdoPrFetcher.ToThreadComment(comment);

        Assert.Equal("6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8", mapped.AuthorNativeId);
        Assert.Equal(Guid.Parse("6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8"), mapped.AuthorId);
        Assert.Equal("Octo Dev", mapped.AuthorName);
    }

    // An identifier this adapter cannot parse as a GUID still names the account, so it is carried as text
    // rather than dropped along with the parse.
    [Fact]
    public void ToThreadComment_UnparsableIdentityId_StillCarriesIt()
    {
        var comment = new Comment
        {
            Id = 502,
            Content = "Please handle null.",
            Author = new IdentityRef { Id = "aad-object-id", DisplayName = "Octo Dev" },
        };

        var mapped = AdoPrFetcher.ToThreadComment(comment);

        Assert.Equal("aad-object-id", mapped.AuthorNativeId);
        Assert.Null(mapped.AuthorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToThreadComment_WithoutIdentityId_CarriesNoNativeId(string? identityId)
    {
        var comment = new Comment
        {
            Id = 503,
            Content = "Please handle null.",
            Author = identityId is null ? null : new IdentityRef { Id = identityId, DisplayName = "Octo Dev" },
        };

        Assert.Null(AdoPrFetcher.ToThreadComment(comment).AuthorNativeId);
    }

    [Fact]
    public void PullRequest_MetadataIsCorrectlyMapped()
    {
        var pr = new PullRequest(
            "https://dev.azure.com/myorg",
            "my-project",
            "my-repo",
            "my-repo",
            100,
            2,
            "Feature: Add new thing",
            "This adds a new thing.",
            "refs/heads/feature/new-thing",
            "refs/heads/main",
            new List<ChangedFile>().AsReadOnly());

        Assert.Equal("https://dev.azure.com/myorg", pr.OrganizationUrl);
        Assert.Equal("my-project", pr.ProjectId);
        Assert.Equal("my-repo", pr.RepositoryId);
        Assert.Equal(100, pr.PullRequestId);
        Assert.Equal(2, pr.IterationId);
        Assert.Equal("Feature: Add new thing", pr.Title);
        Assert.Equal("This adds a new thing.", pr.Description);
        Assert.Equal("refs/heads/feature/new-thing", pr.SourceBranch);
        Assert.Equal("refs/heads/main", pr.TargetBranch);
    }

    [Fact]
    public void PullRequest_WithEmptyChangedFiles_IsValid()
    {
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            1,
            "My PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly());

        Assert.Equal(42, pr.PullRequestId);
        Assert.Empty(pr.ChangedFiles);
    }

    [Fact]
    public void PullRequest_WithMultipleChangedFiles_AllIncluded()
    {
        var files = new List<ChangedFile>
        {
            new("/src/A.cs", ChangeType.Add, "content a", ""),
            new("/src/B.cs", ChangeType.Edit, "content b", "diff"),
            new("/src/C.cs", ChangeType.Delete, "", "- deleted"),
        }.AsReadOnly();

        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Multi-file PR",
            "desc",
            "feature/z",
            "main",
            files);

        Assert.Equal(3, pr.ChangedFiles.Count);
    }

    [Fact]
    public void PullRequest_WithDeltaChangedFiles_PreservesFullManifestSeparately()
    {
        var deltaFiles = new List<ChangedFile>
        {
            new("/src/Changed.cs", ChangeType.Edit, "updated content", "- old\n+ new"),
        }.AsReadOnly();
        var fullManifest = new List<ChangedFileSummary>
        {
            new("/src/Changed.cs", ChangeType.Edit),
            new("/src/Stable.cs", ChangeType.Edit),
        }.AsReadOnly();

        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            2,
            "Delta PR",
            null,
            "feature/x",
            "main",
            deltaFiles,
            AllChangedFileSummaries: fullManifest);

        Assert.Single(pr.ChangedFiles);
        Assert.Collection(
            pr.AllPrFileSummaries,
            item =>
            {
                Assert.Equal("/src/Changed.cs", item.Path);
                Assert.Equal(ChangeType.Edit, item.ChangeType);
            },
            item =>
            {
                Assert.Equal("/src/Stable.cs", item.Path);
                Assert.Equal(ChangeType.Edit, item.ChangeType);
            });
    }
}
