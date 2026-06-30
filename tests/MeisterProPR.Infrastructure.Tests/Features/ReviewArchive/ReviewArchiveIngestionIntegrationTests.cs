// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Features.ReviewArchive;

/// <summary>
///     Component-integration coverage for the retention ingestion path: the real
///     <see cref="ReviewArchiveIngestionService" /> over the real <see cref="ReviewArchiveStore" />,
///     a real <see cref="ISecretProtectionCodec" />, and a real (in-memory) EF context. Only the event
///     producer is faked — everything from the domain event down to the persisted, encrypted rows is the
///     production code path. These tests run in the commit gate without a database container.
/// </summary>
public sealed class ReviewArchiveIngestionIntegrationTests : IDisposable
{
    private readonly ISecretProtectionCodec _codec;
    private readonly MeisterProPRDbContext _dbContext;
    private readonly ReviewArchiveIngestionService _ingestion;
    private readonly ReviewArchiveStore _store;

    public ReviewArchiveIngestionIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ReviewArchiveIngestionIntegrationTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._codec = CreateCodec();
        this._store = new ReviewArchiveStore(this._dbContext, this._codec);
        this._ingestion = new ReviewArchiveIngestionService(this._store);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task HandleThreadUpdatedAsync_PersistsThroughRealStore_RoundTripsDecryptedAndEncryptsAtRest()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-ingest";
        const long pullRequestId = 4242;
        const string humanBody = "Please rename this method for clarity.";
        const string aiBody = "I have updated the suggestion based on your feedback.";

        var publishedFirst = DateTimeOffset.UtcNow.AddMinutes(-10);
        var publishedSecond = DateTimeOffset.UtcNow.AddMinutes(-9);

        var evt = new ThreadUpdatedEvent(
            clientId,
            connectionId,
            repositoryId,
            pullRequestId,
            "thread-77",
            "src/Domain/Order.cs",
            128,
            "active",
            publishedSecond,
            [
                new ThreadUpdatedComment("comment-1", "alice@example.com", false, publishedFirst, humanBody),
                new ThreadUpdatedComment("comment-2", "propr-bot", true, publishedSecond, aiBody),
            ]);

        await this._ingestion.HandleThreadUpdatedAsync(evt);

        var key = new PullRequestRetentionKey(clientId, connectionId, repositoryId, pullRequestId);
        var threads = await this._store.GetThreadsForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);

        var thread = Assert.Single(threads);
        Assert.Equal("thread-77", thread.ThreadId);
        Assert.Equal("src/Domain/Order.cs", thread.FilePath);
        Assert.Equal(128, thread.Line);
        Assert.Equal("active", thread.Status);
        Assert.Equal(publishedSecond, thread.UpdatedAt);

        Assert.Equal(2, thread.Comments.Count);

        var human = thread.Comments[0];
        Assert.Equal("comment-1", human.CommentId);
        Assert.Equal("alice@example.com", human.AuthorIdentity);
        Assert.False(human.IsAiAuthored);
        Assert.Equal(humanBody, human.Text);

        var ai = thread.Comments[1];
        Assert.Equal("comment-2", ai.CommentId);
        Assert.True(ai.IsAiAuthored);
        Assert.Equal(aiBody, ai.Text);

        // The parent retained pull request rolled up the producer-supplied status and activity.
        var persistedPullRequest = await this._dbContext.RetainedPullRequests.AsNoTracking().SingleAsync();
        Assert.Equal("active", persistedPullRequest.PrState);
        Assert.Equal(publishedSecond, persistedPullRequest.LastActivityAt);

        // The comment bodies must be encrypted at rest, never persisted as plaintext.
        var persistedComments = await this._dbContext.RetainedThreadComments.AsNoTracking().ToListAsync();
        Assert.Equal(2, persistedComments.Count);
        foreach (var persisted in persistedComments)
        {
            Assert.True(this._codec.IsProtected(persisted.EncryptedText));
        }

        Assert.DoesNotContain(persistedComments, c => c.EncryptedText.Contains(humanBody, StringComparison.Ordinal));
        Assert.DoesNotContain(persistedComments, c => c.EncryptedText.Contains(aiBody, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleReviewIncrementDiffsAsync_PersistsThroughRealStore_ListsAndServesNewestRevision()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-diffs";
        const long pullRequestId = 7;

        const string firstDiff = "@@ -1,2 +1,2 @@\n-var a = 1;\n+var a = 2;";
        const string secondDiff = "@@ -1,2 +1,3 @@\n-var a = 2;\n+var a = 3;\n+var b = 4;";

        var firstActivity = DateTimeOffset.UtcNow.AddMinutes(-20);
        var secondActivity = DateTimeOffset.UtcNow.AddMinutes(-5);

        await this._ingestion.HandleReviewIncrementDiffsAsync(
            new ReviewIncrementCompletedEvent(
                clientId,
                connectionId,
                repositoryId,
                pullRequestId,
                "rev-1",
                "open",
                firstActivity,
                [
                    new ReviewIncrementFileDiff("src/Calc.cs", "Modified", false, firstDiff),
                    new ReviewIncrementFileDiff("assets/icon.png", "Added", true, string.Empty),
                ]));

        await this._ingestion.HandleReviewIncrementDiffsAsync(
            new ReviewIncrementCompletedEvent(
                clientId,
                connectionId,
                repositoryId,
                pullRequestId,
                "rev-2",
                "open",
                secondActivity,
                [new ReviewIncrementFileDiff("src/Calc.cs", "Modified", false, secondDiff)]));

        var key = new PullRequestRetentionKey(clientId, connectionId, repositoryId, pullRequestId);

        var files = await this._store.ListRetainedFilesForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);
        Assert.Equal(2, files.Count);

        var sourceFile = files.Single(file => file.FilePath == "src/Calc.cs");
        Assert.Equal("rev-2", sourceFile.RevisionKey);
        Assert.Equal("Modified", sourceFile.ChangeType);
        Assert.False(sourceFile.IsBinary);

        var binaryFile = files.Single(file => file.FilePath == "assets/icon.png");
        Assert.Equal("rev-1", binaryFile.RevisionKey);
        Assert.True(binaryFile.IsBinary);

        // No revision supplied -> the newest retained diff for the file is served.
        var newest = await this._store.GetFileDiffAsync(key.ClientId, key.RepositoryId, key.PullRequestId, null, "src/Calc.cs");
        Assert.NotNull(newest);
        Assert.Equal("rev-2", newest!.RevisionKey);
        Assert.Equal(secondDiff, newest.UnifiedDiff);

        // The earlier revision remains addressable by its explicit revision key.
        var older = await this._store.GetFileDiffAsync(key.ClientId, key.RepositoryId, key.PullRequestId, "rev-1", "src/Calc.cs");
        Assert.NotNull(older);
        Assert.Equal(firstDiff, older!.UnifiedDiff);

        // The unified diffs are encrypted at rest.
        var persistedDiffs = await this._dbContext.RetainedFileDiffs.AsNoTracking().ToListAsync();
        Assert.All(
            persistedDiffs.Where(diff => !diff.IsBinary),
            diff => Assert.True(this._codec.IsProtected(diff.EncryptedUnifiedDiff)));
    }

    [Fact]
    public async Task PurgeExpiredForConnection_RemovesStaleArchiveOnly_LeavingMemoryAndReviewRowsUntouched()
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const string repositoryId = "repo-purge";

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);

        // A stale retained pull request (older than the cutoff) ingested through the real path.
        var staleKey = new PullRequestRetentionKey(clientId, connectionId, repositoryId, 1);
        await this._ingestion.HandleThreadUpdatedAsync(
            new ThreadUpdatedEvent(
                clientId,
                connectionId,
                repositoryId,
                1,
                "thread-stale",
                "src/Stale.cs",
                3,
                "resolved",
                cutoff.AddDays(-1),
                [new ThreadUpdatedComment("c-stale", "alice", false, cutoff.AddDays(-1), "stale comment")]));
        await this._ingestion.HandleReviewIncrementDiffsAsync(
            new ReviewIncrementCompletedEvent(
                clientId,
                connectionId,
                repositoryId,
                1,
                "rev-stale",
                "closed",
                cutoff.AddDays(-1),
                [new ReviewIncrementFileDiff("src/Stale.cs", "Modified", false, "@@ stale @@")]));

        // A fresh retained pull request (newer than the cutoff) that must survive.
        var freshKey = new PullRequestRetentionKey(clientId, connectionId, repositoryId, 2);
        await this._ingestion.HandleReviewIncrementDiffsAsync(
            new ReviewIncrementCompletedEvent(
                clientId,
                connectionId,
                repositoryId,
                2,
                "rev-fresh",
                "open",
                cutoff.AddDays(2),
                [new ReviewIncrementFileDiff("src/Fresh.cs", "Modified", false, "@@ fresh @@")]));

        // Unrelated memory and review rows sharing the same context must never be touched by the sweep.
        var memoryRecord = new ThreadMemoryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ThreadId = 999,
            RepositoryId = repositoryId,
            PullRequestId = 1,
            FilePath = "src/Stale.cs",
            CommentHistoryDigest = "alice: stale comment",
            ResolutionSummary = "Resolved by renaming.",
            EmbeddingVector = [0.1f, 0.2f, 0.3f],
            CreatedAt = cutoff.AddDays(-1),
            UpdatedAt = cutoff.AddDays(-1),
            MemorySource = MemorySource.ThreadResolved,
        };
        this._dbContext.ThreadMemoryRecords.Add(memoryRecord);

        var reviewJob = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", repositoryId, 1, 1);
        var reviewFileResult = new ReviewFileResult(reviewJob.Id, "src/Stale.cs");
        reviewFileResult.MarkCompleted(
            "Per-file summary",
            [new ReviewComment("src/Stale.cs", 3, CommentSeverity.Warning, "Consider a clearer name.")]);
        reviewJob.FileReviewResults.Add(reviewFileResult);
        this._dbContext.ReviewJobs.Add(reviewJob);

        await this._dbContext.SaveChangesAsync();

        // Mirror the worker's PurgeExpired decision for a retention-enabled connection.
        var removed = await this._store.PurgeExpiredForConnectionAsync(connectionId, cutoff);

        Assert.Equal(1, removed);

        // The stale archive aggregate and its children are gone.
        Assert.Empty(await this._store.GetThreadsForPullRequestAsync(staleKey.ClientId, staleKey.RepositoryId, staleKey.PullRequestId));
        Assert.Null(await this._store.GetFileDiffAsync(staleKey.ClientId, staleKey.RepositoryId, staleKey.PullRequestId, null, "src/Stale.cs"));

        // The fresh archive aggregate survives.
        Assert.NotNull(await this._store.GetFileDiffAsync(freshKey.ClientId, freshKey.RepositoryId, freshKey.PullRequestId, null, "src/Fresh.cs"));
        Assert.Equal(1, await this._dbContext.RetainedPullRequests.CountAsync());

        // The unrelated memory and review rows are completely untouched.
        var persistedMemory = await this._dbContext.ThreadMemoryRecords.AsNoTracking().SingleAsync();
        Assert.Equal(memoryRecord.Id, persistedMemory.Id);
        Assert.Equal("Resolved by renaming.", persistedMemory.ResolutionSummary);

        var persistedJob = await this._dbContext.ReviewJobs.AsNoTracking().SingleAsync();
        Assert.Equal(reviewJob.Id, persistedJob.Id);
        var persistedFileResult = await this._dbContext.ReviewFileResults.AsNoTracking().SingleAsync();
        Assert.Equal(reviewFileResult.Id, persistedFileResult.Id);
        Assert.Equal("src/Stale.cs", persistedFileResult.FilePath);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.ReviewArchiveIngestionIntegrationTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
