// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Domain.Events;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public sealed class ReviewArchiveIngestionServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task HandleThreadUpdatedAsync_TouchesPullRequestAndUpsertsMappedThread()
    {
        var store = Substitute.For<IReviewArchiveStore>();
        var sut = new ReviewArchiveIngestionService(store);

        var publishedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var lastActivityAt = new DateTimeOffset(2026, 6, 2, 9, 30, 0, TimeSpan.Zero);
        var evt = new ThreadUpdatedEvent(
            ClientId,
            ConnectionId,
            "repo-1",
            42,
            "17",
            "/src/file.ts",
            12,
            "Active",
            lastActivityAt,
            [
                new ThreadUpdatedComment("100", "bot-identity", true, publishedAt, "Bot comment"),
                new ThreadUpdatedComment("101", "human-identity", false, publishedAt.AddMinutes(5), "Human reply"),
            ]);

        await sut.HandleThreadUpdatedAsync(evt);

        await store.Received(1).TouchPullRequestAsync(
            Arg.Is<PullRequestRetentionKey>(key =>
                key.ClientId == ClientId
                && key.ConnectionId == ConnectionId
                && key.RepositoryId == "repo-1"
                && key.PullRequestId == 42),
            "Active",
            lastActivityAt,
            Arg.Any<CancellationToken>());

        await store.Received(1).UpsertThreadAsync(
            Arg.Is<PullRequestRetentionKey>(key =>
                key.ClientId == ClientId
                && key.ConnectionId == ConnectionId
                && key.RepositoryId == "repo-1"
                && key.PullRequestId == 42),
            Arg.Is<RetainedThreadSnapshot>(thread =>
                thread.ThreadId == "17"
                && thread.FilePath == "/src/file.ts"
                && thread.Line == 12
                && thread.Status == "Active"
                && thread.UpdatedAt == lastActivityAt
                && thread.Comments.Count == 2
                && thread.Comments[0].CommentId == "100"
                && thread.Comments[0].AuthorIdentity == "bot-identity"
                && thread.Comments[0].IsAiAuthored
                && thread.Comments[0].Text == "Bot comment"
                && thread.Comments[1].CommentId == "101"
                && !thread.Comments[1].IsAiAuthored
                && thread.Comments[1].Text == "Human reply"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewIncrementDiffsAsync_TouchesPullRequestAndSavesMappedFileDiffs()
    {
        var store = Substitute.For<IReviewArchiveStore>();
        var sut = new ReviewArchiveIngestionService(store);

        var lastActivityAt = new DateTimeOffset(2026, 6, 3, 8, 15, 0, TimeSpan.Zero);
        var evt = new ReviewIncrementCompletedEvent(
            ClientId,
            ConnectionId,
            "repo-1",
            42,
            "rev-key-7",
            "Active",
            lastActivityAt,
            [
                new ReviewIncrementFileDiff("src/file.ts", "Modified", false, "@@ -1 +1 @@\n-old\n+new"),
                new ReviewIncrementFileDiff("assets/logo.png", "Added", true, string.Empty),
            ]);

        await sut.HandleReviewIncrementDiffsAsync(evt);

        await store.Received(1).TouchPullRequestAsync(
            Arg.Is<PullRequestRetentionKey>(key =>
                key.ClientId == ClientId
                && key.ConnectionId == ConnectionId
                && key.RepositoryId == "repo-1"
                && key.PullRequestId == 42),
            "Active",
            lastActivityAt,
            Arg.Any<CancellationToken>());

        await store.Received(1).SaveFileDiffsAsync(
            Arg.Is<PullRequestRetentionKey>(key =>
                key.ClientId == ClientId
                && key.ConnectionId == ConnectionId
                && key.RepositoryId == "repo-1"
                && key.PullRequestId == 42),
            "rev-key-7",
            Arg.Is<IReadOnlyList<RetainedFileDiffSnapshot>>(fileDiffs =>
                fileDiffs.Count == 2
                && fileDiffs[0].FilePath == "src/file.ts"
                && fileDiffs[0].ChangeType == "Modified"
                && !fileDiffs[0].IsBinary
                && fileDiffs[0].UnifiedDiff == "@@ -1 +1 @@\n-old\n+new"
                && fileDiffs[1].FilePath == "assets/logo.png"
                && fileDiffs[1].ChangeType == "Added"
                && fileDiffs[1].IsBinary
                && fileDiffs[1].UnifiedDiff == string.Empty),
            Arg.Any<CancellationToken>());
    }
}
