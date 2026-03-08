using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Tests for AdoPullRequestFetcher mapping logic.
///     Since GitHttpClient is sealed, these tests verify the domain mapping helpers
///     used by the fetcher and overall integration shape.
///     Full integration tests against a real ADO instance are out of scope for CI.
/// </summary>
public class AdoPullRequestFetcherTests
{
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

    [Fact]
    public void PullRequest_MetadataIsCorrectlyMapped()
    {
        var pr = new PullRequest(
            "https://dev.azure.com/myorg",
            "my-project",
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
            1,
            1,
            "Multi-file PR",
            "desc",
            "feature/z",
            "main",
            files);

        Assert.Equal(3, pr.ChangedFiles.Count);
    }
}