// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Tracks the per-PR comment watermark for mention scanning.
///     One row per pull request within a crawl configuration scope.
///     If a PR's <c>LastUpdatedAt</c> is not newer than <see cref="LastCommentSeenAt" />,
///     the PR is skipped on the next scan cycle (no ADO thread-fetch call).
/// </summary>
public sealed class MentionPrScan
{
    /// <summary>
    ///     Creates a new <see cref="MentionPrScan" />.
    /// </summary>
    public MentionPrScan(
        Guid id,
        Guid crawlConfigurationId,
        string repositoryId,
        int pullRequestId,
        DateTimeOffset lastCommentSeenAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (crawlConfigurationId == Guid.Empty)
        {
            throw new ArgumentException("CrawlConfigurationId must not be empty.", nameof(crawlConfigurationId));
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            throw new ArgumentException("RepositoryId required.", nameof(repositoryId));
        }

        if (pullRequestId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestId));
        }

        this.Id = id;
        this.CrawlConfigurationId = crawlConfigurationId;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.LastCommentSeenAt = lastCommentSeenAt;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>FK to the crawl configuration this watermark belongs to.</summary>
    public Guid CrawlConfigurationId { get; init; }

    /// <summary>ADO repository identifier.</summary>
    public string RepositoryId { get; init; }

    /// <summary>ADO pull request number.</summary>
    public int PullRequestId { get; init; }

    /// <summary>
    ///     Latest comment timestamp observed on this PR.
    ///     Used to skip thread-fetch calls when a PR has not received new comments.
    /// </summary>
    public DateTimeOffset LastCommentSeenAt { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
