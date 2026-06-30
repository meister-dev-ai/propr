// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public sealed class ReviewArchiveStoreTests : IDisposable
{
    private readonly ISecretProtectionCodec _codec;
    private readonly MeisterProPRDbContext _dbContext;
    private readonly ReviewArchiveStore _store;

    public ReviewArchiveStoreTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ReviewArchiveStoreTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._codec = CreateCodec();
        this._store = new ReviewArchiveStore(this._dbContext, this._codec);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task UpsertThreadAsync_RoundTripsThreadAndDecryptsComments()
    {
        var key = NewKey();
        var thread = new RetainedThreadSnapshot(
            "thread-1",
            "src/Service.cs",
            42,
            "active",
            DateTimeOffset.UtcNow,
            [
                new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow.AddMinutes(-2), "First comment"),
                new RetainedCommentSnapshot("c2", "propr-bot", true, DateTimeOffset.UtcNow.AddMinutes(-1), "AI reply"),
            ]);

        await this._store.UpsertThreadAsync(key, thread);

        var threads = await this._store.GetThreadsForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);
        var stored = Assert.Single(threads);
        Assert.Equal("thread-1", stored.ThreadId);
        Assert.Equal("src/Service.cs", stored.FilePath);
        Assert.Equal(42, stored.Line);
        Assert.Equal("active", stored.Status);
        Assert.Equal(2, stored.Comments.Count);
        Assert.Equal("First comment", stored.Comments[0].Text);
        Assert.Equal("alice", stored.Comments[0].AuthorIdentity);
        Assert.False(stored.Comments[0].IsAiAuthored);
        Assert.Equal("AI reply", stored.Comments[1].Text);
        Assert.True(stored.Comments[1].IsAiAuthored);
    }

    [Fact]
    public async Task UpsertThreadAsync_PersistsOriginatingJobId_AndGetThreadsReturnsIt()
    {
        var key = NewKey();
        var originatingJobId = Guid.NewGuid();
        var thread = new RetainedThreadSnapshot(
            "thread-1",
            null,
            null,
            "active",
            DateTimeOffset.UtcNow,
            [
                new RetainedCommentSnapshot("c1", "propr-bot", true, DateTimeOffset.UtcNow.AddMinutes(-2), "AI finding", originatingJobId),
                new RetainedCommentSnapshot("c2", "alice", false, DateTimeOffset.UtcNow.AddMinutes(-1), "Human reply"),
            ]);

        await this._store.UpsertThreadAsync(key, thread);

        var threads = await this._store.GetThreadsForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);
        var stored = Assert.Single(threads);
        Assert.Equal(originatingJobId, stored.Comments[0].OriginatingJobId);
        Assert.Null(stored.Comments[1].OriginatingJobId);
    }

    [Fact]
    public async Task UpsertThreadAsync_StoresCommentBodyEncrypted()
    {
        var key = NewKey();
        const string plaintext = "Sensitive comment body";
        var thread = new RetainedThreadSnapshot(
            "thread-1",
            null,
            null,
            "active",
            DateTimeOffset.UtcNow,
            [new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow, plaintext)]);

        await this._store.UpsertThreadAsync(key, thread);

        var persisted = await this._dbContext.RetainedThreadComments.AsNoTracking().SingleAsync();
        Assert.NotEqual(plaintext, persisted.EncryptedText);
        Assert.DoesNotContain(plaintext, persisted.EncryptedText, StringComparison.Ordinal);
        Assert.True(this._codec.IsProtected(persisted.EncryptedText));
    }

    [Fact]
    public async Task UpsertThreadAsync_RepeatedSnapshot_ReplacesComments()
    {
        var key = NewKey();
        await this._store.UpsertThreadAsync(
            key,
            new RetainedThreadSnapshot(
                "thread-1",
                null,
                null,
                "active",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                [
                    new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow.AddMinutes(-5), "Old one"),
                    new RetainedCommentSnapshot("c2", "bob", false, DateTimeOffset.UtcNow.AddMinutes(-4), "Old two"),
                ]));

        await this._store.UpsertThreadAsync(
            key,
            new RetainedThreadSnapshot(
                "thread-1",
                null,
                null,
                "resolved",
                DateTimeOffset.UtcNow,
                [new RetainedCommentSnapshot("c3", "carol", false, DateTimeOffset.UtcNow, "Only one now")]));

        var threads = await this._store.GetThreadsForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);
        var stored = Assert.Single(threads);
        Assert.Equal("resolved", stored.Status);
        var comment = Assert.Single(stored.Comments);
        Assert.Equal("Only one now", comment.Text);

        // The replaced comments must not linger in the store.
        Assert.Equal(1, await this._dbContext.RetainedThreadComments.CountAsync());
    }

    [Fact]
    public async Task SaveFileDiffsAsync_RoundTripsAndDecryptsNewestRevision()
    {
        var key = NewKey();
        const string oldDiff = "@@ -1 +1 @@\n-old\n+older";
        const string newDiff = "@@ -1 +1 @@\n-old\n+newest";

        await this._store.SaveFileDiffsAsync(
            key,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, oldDiff)]);
        await this._store.SaveFileDiffsAsync(
            key,
            "rev-2",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, newDiff)]);

        var byRevision = await this._store.GetFileDiffAsync(key.ClientId, key.RepositoryId, key.PullRequestId, "rev-1", "src/A.cs");
        Assert.NotNull(byRevision);
        Assert.Equal(oldDiff, byRevision!.UnifiedDiff);

        // No revision specified resolves to the newest retained revision.
        var newest = await this._store.GetFileDiffAsync(key.ClientId, key.RepositoryId, key.PullRequestId, null, "src/A.cs");
        Assert.NotNull(newest);
        Assert.Equal("rev-2", newest!.RevisionKey);
        Assert.Equal(newDiff, newest.UnifiedDiff);
    }

    [Fact]
    public async Task SaveFileDiffsAsync_StoresUnifiedDiffEncrypted()
    {
        var key = NewKey();
        const string plaintext = "@@ -1 +1 @@\n-secret\n+also-secret";

        await this._store.SaveFileDiffsAsync(
            key,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, plaintext)]);

        var persisted = await this._dbContext.RetainedFileDiffs.AsNoTracking().SingleAsync();
        Assert.NotEqual(plaintext, persisted.EncryptedUnifiedDiff);
        Assert.DoesNotContain("secret", persisted.EncryptedUnifiedDiff, StringComparison.Ordinal);
        Assert.True(this._codec.IsProtected(persisted.EncryptedUnifiedDiff));
    }

    [Fact]
    public async Task ListRetainedFilesForPullRequestAsync_CollapsesToNewestRevisionPerFile_WithoutDiffText()
    {
        var key = NewKey();

        await this._store.SaveFileDiffsAsync(
            key,
            "rev-1",
            [
                new RetainedFileDiffSnapshot("src/A.cs", "edit", false, "@@ a-old @@"),
                new RetainedFileDiffSnapshot("assets/logo.png", "add", true, string.Empty),
            ]);
        await this._store.SaveFileDiffsAsync(
            key,
            "rev-2",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, "@@ a-new @@")]);

        var files = await this._store.ListRetainedFilesForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);

        Assert.Equal(2, files.Count);

        var sourceFile = files.Single(file => file.FilePath == "src/A.cs");
        Assert.Equal("rev-2", sourceFile.RevisionKey);
        Assert.Equal("edit", sourceFile.ChangeType);
        Assert.False(sourceFile.IsBinary);

        var binaryFile = files.Single(file => file.FilePath == "assets/logo.png");
        Assert.Equal("rev-1", binaryFile.RevisionKey);
        Assert.True(binaryFile.IsBinary);
    }

    [Fact]
    public async Task ListRetainedFilesForPullRequestAsync_WhenPullRequestNotRetained_ReturnsEmpty()
    {
        var key = NewKey();
        var files = await this._store.ListRetainedFilesForPullRequestAsync(key.ClientId, key.RepositoryId, key.PullRequestId);

        Assert.Empty(files);
    }

    [Fact]
    public async Task GetThreadsForPullRequestAsync_ResolvesByClientRepositoryAndPullRequest_WithoutConnectionId()
    {
        var clientId = Guid.NewGuid();
        var key = new PullRequestRetentionKey(clientId, Guid.NewGuid(), "repo-1", 100);
        await this._store.UpsertThreadAsync(
            key,
            new RetainedThreadSnapshot(
                "thread-1",
                "src/A.cs",
                7,
                "active",
                DateTimeOffset.UtcNow,
                [new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow, "Hello")]));

        // The read carries no connection id; the store resolves the owning pull request from the
        // client-scoped identity alone.
        var threads = await this._store.GetThreadsForPullRequestAsync(clientId, "repo-1", 100);

        var stored = Assert.Single(threads);
        Assert.Equal("thread-1", stored.ThreadId);
    }

    [Fact]
    public async Task GetFileDiffAsync_WhenTwoConnectionsRetainedSamePullRequest_ReturnsMostRecentlyActive()
    {
        var clientId = Guid.NewGuid();
        const string repositoryId = "repo-shared";
        const long pullRequestId = 7;

        // Two connections retained the same repository + pull request. The newer connection's diff
        // must win because the read resolves to the most recently active retained pull request.
        var olderKey = new PullRequestRetentionKey(clientId, Guid.NewGuid(), repositoryId, pullRequestId);
        var newerKey = new PullRequestRetentionKey(clientId, Guid.NewGuid(), repositoryId, pullRequestId);

        await this._store.TouchPullRequestAsync(olderKey, "open", DateTimeOffset.UtcNow.AddDays(-2));
        await this._store.SaveFileDiffsAsync(
            olderKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, "@@ older-connection @@")]);

        await this._store.TouchPullRequestAsync(newerKey, "open", DateTimeOffset.UtcNow);
        await this._store.SaveFileDiffsAsync(
            newerKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, "@@ newer-connection @@")]);

        var diff = await this._store.GetFileDiffAsync(clientId, repositoryId, pullRequestId, null, "src/A.cs");

        Assert.NotNull(diff);
        Assert.Equal("@@ newer-connection @@", diff!.UnifiedDiff);
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesStalePullRequestAndCascades_LeavingNewerIntact()
    {
        var staleKey = new PullRequestRetentionKey(Guid.NewGuid(), Guid.NewGuid(), "repo-stale", 1);
        var freshKey = new PullRequestRetentionKey(staleKey.ClientId, staleKey.ConnectionId, "repo-fresh", 2);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);

        await this._store.TouchPullRequestAsync(staleKey, "closed", cutoff.AddDays(-1));
        await this._store.UpsertThreadAsync(
            staleKey,
            new RetainedThreadSnapshot(
                "t-stale",
                null,
                null,
                "resolved",
                DateTimeOffset.UtcNow,
                [new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow, "stale comment")]));
        await this._store.SaveFileDiffsAsync(
            staleKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Stale.cs", "edit", false, "@@ stale @@")]);

        await this._store.TouchPullRequestAsync(freshKey, "open", cutoff.AddDays(1));
        await this._store.UpsertThreadAsync(
            freshKey,
            new RetainedThreadSnapshot(
                "t-fresh",
                null,
                null,
                "active",
                DateTimeOffset.UtcNow,
                [new RetainedCommentSnapshot("c2", "bob", false, DateTimeOffset.UtcNow, "fresh comment")]));
        await this._store.SaveFileDiffsAsync(
            freshKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Fresh.cs", "edit", false, "@@ fresh @@")]);

        var removed = await this._store.PurgeExpiredAsync(cutoff);

        Assert.Equal(1, removed);

        // The stale aggregate and all of its children are gone.
        Assert.False(await this._store.GetThreadsForPullRequestAsync(staleKey.ClientId, staleKey.RepositoryId, staleKey.PullRequestId) is { Count: > 0 });
        Assert.Null(await this._store.GetFileDiffAsync(staleKey.ClientId, staleKey.RepositoryId, staleKey.PullRequestId, null, "src/Stale.cs"));

        // The fresh aggregate is untouched.
        var freshThreads = await this._store.GetThreadsForPullRequestAsync(freshKey.ClientId, freshKey.RepositoryId, freshKey.PullRequestId);
        Assert.Single(freshThreads);
        Assert.NotNull(await this._store.GetFileDiffAsync(freshKey.ClientId, freshKey.RepositoryId, freshKey.PullRequestId, null, "src/Fresh.cs"));

        // Only the fresh aggregate's rows remain in the store.
        Assert.Equal(1, await this._dbContext.RetainedPullRequests.CountAsync());
        Assert.Equal(1, await this._dbContext.RetainedThreads.CountAsync());
        Assert.Equal(1, await this._dbContext.RetainedThreadComments.CountAsync());
        Assert.Equal(1, await this._dbContext.RetainedFileDiffs.CountAsync());
    }

    [Fact]
    public async Task PurgeForConnectionAsync_RemovesAllRetainedDataForConnection()
    {
        var clientId = Guid.NewGuid();
        var targetConnection = Guid.NewGuid();
        var otherConnection = Guid.NewGuid();

        var targetKey = new PullRequestRetentionKey(clientId, targetConnection, "repo", 1);
        var otherKey = new PullRequestRetentionKey(clientId, otherConnection, "repo", 2);

        await this._store.TouchPullRequestAsync(targetKey, "open", DateTimeOffset.UtcNow);
        await this._store.SaveFileDiffsAsync(
            targetKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/A.cs", "edit", false, "@@ a @@")]);
        await this._store.TouchPullRequestAsync(otherKey, "open", DateTimeOffset.UtcNow);

        var removed = await this._store.PurgeForConnectionAsync(targetConnection);

        Assert.Equal(1, removed);
        Assert.Null(await this._store.GetFileDiffAsync(targetKey.ClientId, targetKey.RepositoryId, targetKey.PullRequestId, null, "src/A.cs"));
        Assert.Equal(1, await this._dbContext.RetainedPullRequests.CountAsync());
    }

    [Fact]
    public async Task PurgeExpiredForConnectionAsync_RemovesStalePrForConnection_LeavingNewerAndOtherConnectionsIntact()
    {
        var clientId = Guid.NewGuid();
        var targetConnection = Guid.NewGuid();
        var otherConnection = Guid.NewGuid();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);

        // Stale, open PR on the target connection (open PRs are not exempt).
        var staleKey = new PullRequestRetentionKey(clientId, targetConnection, "repo-stale", 1);
        await this._store.TouchPullRequestAsync(staleKey, "open", cutoff.AddDays(-1));
        await this._store.UpsertThreadAsync(
            staleKey,
            new RetainedThreadSnapshot(
                "t-stale",
                null,
                null,
                "active",
                DateTimeOffset.UtcNow,
                [new RetainedCommentSnapshot("c1", "alice", false, DateTimeOffset.UtcNow, "stale comment")]));
        await this._store.SaveFileDiffsAsync(
            staleKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Stale.cs", "edit", false, "@@ stale @@")]);

        // Newer PR on the same connection: must survive.
        var freshKey = new PullRequestRetentionKey(clientId, targetConnection, "repo-fresh", 2);
        await this._store.TouchPullRequestAsync(freshKey, "open", cutoff.AddDays(1));
        await this._store.SaveFileDiffsAsync(
            freshKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Fresh.cs", "edit", false, "@@ fresh @@")]);

        // Stale PR on a different connection: must survive (cutoff is scoped to the connection).
        var otherKey = new PullRequestRetentionKey(clientId, otherConnection, "repo-other", 3);
        await this._store.TouchPullRequestAsync(otherKey, "open", cutoff.AddDays(-5));
        await this._store.SaveFileDiffsAsync(
            otherKey,
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Other.cs", "edit", false, "@@ other @@")]);

        var removed = await this._store.PurgeExpiredForConnectionAsync(targetConnection, cutoff);

        Assert.Equal(1, removed);

        // The stale aggregate and its children are gone.
        Assert.False(await this._store.GetThreadsForPullRequestAsync(staleKey.ClientId, staleKey.RepositoryId, staleKey.PullRequestId) is { Count: > 0 });
        Assert.Null(await this._store.GetFileDiffAsync(staleKey.ClientId, staleKey.RepositoryId, staleKey.PullRequestId, null, "src/Stale.cs"));

        // The newer PR on the same connection and the other connection's PR are untouched.
        Assert.NotNull(await this._store.GetFileDiffAsync(freshKey.ClientId, freshKey.RepositoryId, freshKey.PullRequestId, null, "src/Fresh.cs"));
        Assert.NotNull(await this._store.GetFileDiffAsync(otherKey.ClientId, otherKey.RepositoryId, otherKey.PullRequestId, null, "src/Other.cs"));

        Assert.Equal(2, await this._dbContext.RetainedPullRequests.CountAsync());
        Assert.Equal(0, await this._dbContext.RetainedThreads.CountAsync());
        Assert.Equal(0, await this._dbContext.RetainedThreadComments.CountAsync());
        Assert.Equal(2, await this._dbContext.RetainedFileDiffs.CountAsync());
    }

    private static PullRequestRetentionKey NewKey()
    {
        return new PullRequestRetentionKey(Guid.NewGuid(), Guid.NewGuid(), "repo-1", 100);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterProPR.ReviewArchiveStoreTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
