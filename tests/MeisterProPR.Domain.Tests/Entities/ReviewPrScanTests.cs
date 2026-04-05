// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Domain.Tests.Entities;

public class ReviewPrScanTests
{
    private static ReviewPrScan CreateScan(
        Guid? id = null,
        Guid? clientId = null,
        string repositoryId = "repo-1",
        int pullRequestId = 1,
        string lastProcessedCommitId = "abc123")
    {
        return new ReviewPrScan(
            id ?? Guid.NewGuid(),
            clientId ?? Guid.NewGuid(),
            repositoryId,
            pullRequestId,
            lastProcessedCommitId);
    }

    [Fact]
    public void Constructor_SetsAllFields()
    {
        var id = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var scan = new ReviewPrScan(id, clientId, "my-repo", 42, "sha-abc");

        Assert.Equal(id, scan.Id);
        Assert.Equal(clientId, scan.ClientId);
        Assert.Equal("my-repo", scan.RepositoryId);
        Assert.Equal(42, scan.PullRequestId);
        Assert.Equal("sha-abc", scan.LastProcessedCommitId);
    }

    [Fact]
    public void Constructor_SetsUpdatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var scan = CreateScan();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.InRange(scan.UpdatedAt, before, after);
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyId()
    {
        Assert.Throws<ArgumentException>(() => CreateScan(id: Guid.Empty));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyClientId()
    {
        Assert.Throws<ArgumentException>(() => CreateScan(clientId: Guid.Empty));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyRepositoryId()
    {
        Assert.Throws<ArgumentException>(() => CreateScan(repositoryId: ""));
    }

    [Fact]
    public void Constructor_ThrowsOnWhitespaceRepositoryId()
    {
        Assert.Throws<ArgumentException>(() => CreateScan(repositoryId: "   "));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroPullRequestId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScan(pullRequestId: 0));
    }

    [Fact]
    public void Constructor_ThrowsOnNegativePullRequestId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateScan(pullRequestId: -1));
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrEmptyLastProcessedCommitId()
    {
        Assert.Throws<ArgumentException>(() => CreateScan(lastProcessedCommitId: ""));
    }

    [Fact]
    public void LastProcessedCommitId_CanBeUpdated()
    {
        var scan = CreateScan();
        scan.LastProcessedCommitId = "newsha";
        Assert.Equal("newsha", scan.LastProcessedCommitId);
    }

    [Fact]
    public void UpdatedAt_CanBeUpdated()
    {
        var scan = CreateScan();
        var newTime = DateTimeOffset.UtcNow.AddHours(1);
        scan.UpdatedAt = newTime;
        Assert.Equal(newTime, scan.UpdatedAt);
    }

    [Fact]
    public void Threads_IsEmptyByDefault()
    {
        var scan = CreateScan();
        Assert.Empty(scan.Threads);
    }
}
