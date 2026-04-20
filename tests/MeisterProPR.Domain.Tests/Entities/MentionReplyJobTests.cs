// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Entities;

public sealed class MentionReplyJobTests
{
    [Fact]
    public void Constructor_PopulatesProviderNeutralCompatibilityFields()
    {
        var job = new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            42,
            7,
            9,
            "@meister-bot please help");

        Assert.Equal(ScmProvider.AzureDevOps, job.Provider);
        Assert.Equal("https://dev.azure.com", job.HostBaseUrl);
        Assert.Equal("proj", job.RepositoryOwnerOrNamespace);
        Assert.Equal("proj", job.RepositoryProjectPath);
        Assert.Equal(CodeReviewPlatformKind.PullRequest, job.CodeReviewPlatformKind);
        Assert.Equal("42", job.ExternalCodeReviewId);
        Assert.Equal("7", job.ReviewThreadReference.ExternalThreadId);
        Assert.Null(job.ReviewCommentReference);
    }

    [Fact]
    public void Constructor_WithAuthorAndLocation_PopulatesNeutralCommentReference()
    {
        var authorId = Guid.NewGuid();
        var publishedAt = DateTimeOffset.Parse("2026-04-08T09:00:00Z");

        var job = new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            42,
            7,
            9,
            "@meister-bot please help",
            "src/file.cs",
            18,
            authorId,
            "Ada Lovelace",
            publishedAt);

        Assert.Equal("src/file.cs", job.ReviewThreadReference.FilePath);
        Assert.Equal(18, job.ReviewThreadReference.LineNumber);
        Assert.NotNull(job.ReviewCommentReference);
        Assert.Equal("9", job.ReviewCommentReference!.ExternalCommentId);
        Assert.Equal(authorId.ToString("D"), job.ReviewCommentReference.Author.ExternalUserId);
        Assert.Equal("Ada Lovelace", job.ReviewCommentReference.Author.Login);
        Assert.Equal(publishedAt, job.ReviewCommentReference.PublishedAt);
    }
}
