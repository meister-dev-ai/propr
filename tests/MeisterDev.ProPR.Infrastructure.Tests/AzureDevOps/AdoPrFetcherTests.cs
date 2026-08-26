// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
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
