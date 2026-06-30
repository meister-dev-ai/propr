// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public sealed class PostedCommentOriginStoreTests : IDisposable
{
    private readonly MeisterProPRDbContext _dbContext;
    private readonly PostedCommentOriginStore _store;

    public PostedCommentOriginStoreTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"PostedCommentOriginStoreTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new PostedCommentOriginStore(this._dbContext);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task RecordAsync_ThenGetJobIdForComment_RoundTrips()
    {
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var entry = new PostedCommentOriginEntry(
            clientId,
            "repo-1",
            100,
            "thread-7",
            "comment-1",
            jobId,
            DateTimeOffset.UtcNow);

        await this._store.RecordAsync([entry]);

        var resolved = await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-7", "comment-1");

        Assert.Equal(jobId, resolved);
    }

    [Fact]
    public async Task GetJobIdForComment_ReturnsNull_WhenNotRecorded()
    {
        var resolved = await this._store.GetJobIdForCommentAsync(
            Guid.NewGuid(),
            "repo-1",
            100,
            "thread-7",
            "missing-comment");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task RecordAsync_IsIdempotentOnNaturalKey_AndRefreshesJob()
    {
        var clientId = Guid.NewGuid();
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        var key = new Func<Guid, PostedCommentOriginEntry>(jobId => new PostedCommentOriginEntry(
            clientId,
            "repo-1",
            100,
            "thread-7",
            "comment-1",
            jobId,
            DateTimeOffset.UtcNow));

        await this._store.RecordAsync([key(firstJobId)]);
        await this._store.RecordAsync([key(secondJobId)]);

        var rowCount = await this._dbContext.PostedCommentOrigins.CountAsync();
        Assert.Equal(1, rowCount);

        var resolved = await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-7", "comment-1");
        Assert.Equal(secondJobId, resolved);
    }

    [Fact]
    public async Task RecordAsync_SameCommentIdAcrossTwoThreads_InsertsBoth_AndResolvesEachToItsJob()
    {
        // Azure DevOps comment ids are thread-local: one publish can create several threads that each
        // start at comment id 1. Recording them in a single batch must insert two distinct rows (the
        // thread id is part of the natural key), not collide on the unique constraint. Because two origins
        // then share comment id "1", resolution falls back to the thread id to disambiguate them.
        var clientId = Guid.NewGuid();
        var firstThreadJobId = Guid.NewGuid();
        var secondThreadJobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "thread-1", "1", firstThreadJobId, now),
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "thread-2", "1", secondThreadJobId, now),
        ]);

        Assert.Equal(2, await this._dbContext.PostedCommentOrigins.CountAsync());

        Assert.Equal(firstThreadJobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-1", "1"));
        Assert.Equal(secondThreadJobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-2", "1"));

        var rows = await this._store.GetJobIdsForPullRequestAsync(clientId, "repo-1", 100);
        Assert.Equal(2, rows.Count);
        Assert.Equal(firstThreadJobId, rows.Single(row => row.ProviderThreadId == "thread-1" && row.ProviderCommentId == "1").JobId);
        Assert.Equal(secondThreadJobId, rows.Single(row => row.ProviderThreadId == "thread-2" && row.ProviderCommentId == "1").JobId);
    }

    [Fact]
    public async Task GetJobIdForComment_ResolvesOnUniqueCommentId_IgnoringDifferingThreadId()
    {
        // GitHub/GitLab/Forgejo publish the review/discussion id as the provider thread id, but the crawler
        // reports a different thread id (and GitLab may report none at all). Their comment ids are globally
        // unique within the pull request, so a lookup must resolve on the comment id alone and ignore the
        // crawled thread id — a strict (thread, comment) match would silently fail for them.
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "review-9", "555", jobId, now),
        ]);

        // A crawled comment whose thread id differs from the recorded one still resolves by comment id.
        Assert.Equal(jobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-abc", "555"));
        // A null crawled thread id (GitLab) resolves the same way.
        Assert.Equal(jobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, null, "555"));
        // An unknown comment id resolves to nothing regardless of thread id.
        Assert.Null(await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "review-9", "999"));
    }

    [Fact]
    public async Task RecordAsync_DuplicateEntriesInOneBatch_DoNotReachSaveChanges()
    {
        // Two entries sharing the full natural key (same thread + comment) must be deduped before insert,
        // otherwise the second Add would collide on the unique constraint. The last write wins.
        var clientId = Guid.NewGuid();
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "thread-1", "1", firstJobId, now),
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "thread-1", "1", secondJobId, now),
        ]);

        Assert.Equal(1, await this._dbContext.PostedCommentOrigins.CountAsync());
        Assert.Equal(firstJobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-1", "1"));
    }

    [Fact]
    public async Task GetJobIdsForPullRequestAsync_ReturnsRowsForExactPullRequest()
    {
        var clientId = Guid.NewGuid();
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        var otherPrJobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "t-1", "comment-a", firstJobId, now),
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "t-1", "comment-b", secondJobId, now),
            // Different pull request on the same repository: must be excluded.
            new PostedCommentOriginEntry(clientId, "repo-1", 200, "t-9", "comment-c", otherPrJobId, now),
        ]);

        var rows = await this._store.GetJobIdsForPullRequestAsync(clientId, "repo-1", 100);

        Assert.Equal(2, rows.Count);
        Assert.Equal(firstJobId, rows.Single(row => row.ProviderCommentId == "comment-a").JobId);
        Assert.Equal(secondJobId, rows.Single(row => row.ProviderCommentId == "comment-b").JobId);
        Assert.DoesNotContain(rows, row => row.ProviderCommentId == "comment-c");
    }

    [Fact]
    public async Task GetJobIdsForPullRequestAsync_ReturnsEmpty_WhenNoneRetained()
    {
        var rows = await this._store.GetJobIdsForPullRequestAsync(Guid.NewGuid(), "repo-1", 100);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task PurgeForPullRequestAsync_RemovesTargetPrOriginsAndLeavesOthers()
    {
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, null, "comment-a", jobId, now),
            new PostedCommentOriginEntry(clientId, "repo-1", 100, null, "comment-b", jobId, now),
            new PostedCommentOriginEntry(clientId, "repo-1", 200, null, "comment-c", jobId, now),
        ]);

        var removed = await this._store.PurgeForPullRequestAsync(clientId, "repo-1", 100);

        Assert.Equal(2, removed);
        Assert.Null(await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, null, "comment-a"));
        Assert.Null(await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, null, "comment-b"));
        Assert.Equal(jobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 200, null, "comment-c"));
    }

    [Fact]
    public async Task PurgeForPullRequestsAsync_RemovesEveryListedPullRequest()
    {
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, null, "comment-a", jobId, now),
            new PostedCommentOriginEntry(clientId, "repo-2", 300, null, "comment-d", jobId, now),
            new PostedCommentOriginEntry(clientId, "repo-3", 400, null, "comment-e", jobId, now),
        ]);

        var removed = await this._store.PurgeForPullRequestsAsync(
        [
            new PostedCommentOriginPullRequestRef(clientId, "repo-1", 100),
            new PostedCommentOriginPullRequestRef(clientId, "repo-2", 300),
        ]);

        Assert.Equal(2, removed);
        Assert.Null(await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, null, "comment-a"));
        Assert.Null(await this._store.GetJobIdForCommentAsync(clientId, "repo-2", 300, null, "comment-d"));
        Assert.Equal(jobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-3", 400, null, "comment-e"));
    }
}

/// <summary>
///     Proves the context-isolation invariant: when a <see cref="IDbContextFactory{TContext}" /> is wired,
///     every provenance write runs on a fresh factory context, never on the shared request-scoped one. A
///     best-effort provenance write must never leave entities tracked on the shared context that would
///     poison a subsequent unrelated save (the original 23505-then-cascade failure mode).
/// </summary>
public sealed class PostedCommentOriginStoreContextIsolationTests : IDisposable
{
    private readonly MeisterProPRDbContext _injectedContext;
    private readonly DbContextOptions<MeisterProPRDbContext> _options;
    private readonly PostedCommentOriginStore _store;

    public PostedCommentOriginStoreContextIsolationTests()
    {
        // The factory and the injected context share one in-memory database so a write on either is visible
        // to the other, but they are distinct context instances with independent change trackers.
        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"PostedCommentOriginStoreContextIsolationTests-{Guid.NewGuid():N}")
            .Options;
        this._injectedContext = new MeisterProPRDbContext(this._options);
        this._store = new PostedCommentOriginStore(this._injectedContext, new TestDbContextFactory(this._options));
    }

    public void Dispose()
    {
        this._injectedContext.Dispose();
    }

    [Fact]
    public async Task RecordAsync_WithFactory_DoesNotTrackOnSharedContext_AndLeavesSubsequentSaveClean()
    {
        var clientId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // The shared context carries an unrelated, not-yet-saved change, exactly as a request that is part
        // way through review/crawl work would.
        var unrelatedClient = new ClientRecord
        {
            Id = clientId,
            DisplayName = "Unrelated client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this._injectedContext.Clients.Add(unrelatedClient);

        await this._store.RecordAsync(
        [
            new PostedCommentOriginEntry(clientId, "repo-1", 100, "thread-1", "comment-1", jobId, DateTimeOffset.UtcNow),
        ]);

        // The provenance row was inserted on the factory context, so the shared context's change tracker
        // never saw it — only the request's own unrelated change is still pending.
        Assert.Empty(this._injectedContext.ChangeTracker.Entries<PostedCommentOrigin>());
        var pendingClients = this._injectedContext.ChangeTracker.Entries<ClientRecord>().ToList();
        Assert.Single(pendingClients);

        // The subsequent save on the shared context succeeds and persists only the unrelated change; a
        // poisoned context (a leftover failed provenance Add) would have thrown or written a phantom row.
        await this._injectedContext.SaveChangesAsync();

        // Provenance persisted via the factory and is resolvable; the unrelated row saved cleanly.
        Assert.Equal(jobId, await this._store.GetJobIdForCommentAsync(clientId, "repo-1", 100, "thread-1", "comment-1"));
        Assert.Equal(1, await this._injectedContext.PostedCommentOrigins.CountAsync());
        Assert.True(await this._injectedContext.Clients.AnyAsync(client => client.Id == clientId));
    }
}
