// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;

/// <summary>
///     Database-backed store for opt-in-retained raw pull-request data. Raw text (comment bodies and
///     unified diffs) is encrypted at rest via <see cref="ISecretProtectionCodec" />; all structured
///     metadata is persisted as plaintext so it remains queryable.
/// </summary>
/// <remarks>
///     Every operation runs on a fresh context created from the injected
///     <see cref="IDbContextFactory{TContext}" /> when one is available, falling back to the injected
///     request-scoped context when it is null (so tests can pass a single context). Retention is a
///     best-effort archival side-write; isolating it means a failure here can never leave tracked entities
///     behind that poison the shared request-scoped context and break the subsequent review/crawl saves.
/// </remarks>
public sealed class ReviewArchiveStore(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IReviewArchiveStore
{
    private const string ThreadCommentPurpose = "review-archive-thread-comment";
    private const string FileDiffPurpose = "review-archive-file-diff";

    public Task TouchPullRequestAsync(
        PullRequestRetentionKey key,
        string prState,
        DateTimeOffset lastActivityAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return this.WithDbAsync(
            async db =>
            {
                await GetOrCreatePullRequestAsync(db, key, prState, lastActivityAt, ct);
                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task UpsertThreadAsync(
        PullRequestRetentionKey key,
        RetainedThreadSnapshot thread,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(thread);

        return this.WithDbAsync(
            async db =>
            {
                var pullRequest = await GetOrCreatePullRequestAsync(db, key, null, null, ct);

                var existingThread = await db.RetainedThreads
                    .Include(candidate => candidate.Comments)
                    .FirstOrDefaultAsync(
                        candidate => candidate.RetainedPullRequestId == pullRequest.Id && candidate.ThreadId == thread.ThreadId,
                        ct);

                if (existingThread is null)
                {
                    existingThread = new RetainedThread
                    {
                        Id = Guid.NewGuid(),
                        RetainedPullRequestId = pullRequest.Id,
                        ThreadId = thread.ThreadId,
                    };
                    db.RetainedThreads.Add(existingThread);
                }
                else
                {
                    // Replace the comment set so the persisted thread mirrors the snapshot exactly.
                    db.RetainedThreadComments.RemoveRange(existingThread.Comments);
                }

                existingThread.FilePath = thread.FilePath;
                existingThread.Line = thread.Line;
                existingThread.Status = thread.Status;
                existingThread.UpdatedAt = thread.UpdatedAt;

                foreach (var comment in thread.Comments)
                {
                    db.RetainedThreadComments.Add(
                        new RetainedThreadComment
                        {
                            Id = Guid.NewGuid(),
                            RetainedThreadId = existingThread.Id,
                            CommentId = comment.CommentId,
                            AuthorIdentity = comment.AuthorIdentity,
                            IsAiAuthored = comment.IsAiAuthored,
                            PublishedAt = comment.PublishedAt,
                            EncryptedText = secretProtectionCodec.Protect(comment.Text, ThreadCommentPurpose),
                            OriginatingJobId = comment.OriginatingJobId,
                        });
                }

                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task SaveFileDiffsAsync(
        PullRequestRetentionKey key,
        string revisionKey,
        IReadOnlyList<RetainedFileDiffSnapshot> fileDiffs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionKey);
        ArgumentNullException.ThrowIfNull(fileDiffs);

        return this.WithDbAsync(
            async db =>
            {
                var pullRequest = await GetOrCreatePullRequestAsync(db, key, null, null, ct);

                var filePaths = fileDiffs.Select(diff => diff.FilePath).ToList();
                var existingDiffs = await db.RetainedFileDiffs
                    .Where(diff =>
                        diff.RetainedPullRequestId == pullRequest.Id
                        && diff.RevisionKey == revisionKey
                        && filePaths.Contains(diff.FilePath))
                    .ToListAsync(ct);

                if (existingDiffs.Count > 0)
                {
                    db.RetainedFileDiffs.RemoveRange(existingDiffs);
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var diff in fileDiffs)
                {
                    db.RetainedFileDiffs.Add(
                        new RetainedFileDiff
                        {
                            Id = Guid.NewGuid(),
                            RetainedPullRequestId = pullRequest.Id,
                            RevisionKey = revisionKey,
                            FilePath = diff.FilePath,
                            ChangeType = diff.ChangeType,
                            IsBinary = diff.IsBinary,
                            EncryptedUnifiedDiff = secretProtectionCodec.Protect(diff.UnifiedDiff, FileDiffPurpose),
                            CreatedAt = now,
                        });
                }

                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task<IReadOnlyList<RetainedThreadView>> GetThreadsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        return this.WithDbAsync<IReadOnlyList<RetainedThreadView>>(
            async db =>
            {
                var pullRequest = await ResolvePullRequestAsync(db, clientId, repositoryId, pullRequestId, ct);
                if (pullRequest is null)
                {
                    return [];
                }

                var threads = await db.RetainedThreads
                    .AsNoTracking()
                    .Where(thread => thread.RetainedPullRequestId == pullRequest.Id)
                    .Include(thread => thread.Comments)
                    .OrderBy(thread => thread.UpdatedAt)
                    .ToListAsync(ct);

                return threads
                    .Select(thread => new RetainedThreadView(
                        thread.ThreadId,
                        thread.FilePath,
                        thread.Line,
                        thread.Status,
                        thread.UpdatedAt,
                        thread.Comments
                            .OrderBy(comment => comment.PublishedAt)
                            .Select(comment => new RetainedCommentView(
                                comment.CommentId,
                                comment.AuthorIdentity,
                                comment.IsAiAuthored,
                                comment.PublishedAt,
                                secretProtectionCodec.Unprotect(comment.EncryptedText, ThreadCommentPurpose),
                                comment.OriginatingJobId))
                            .ToList()))
                    .ToList();
            },
            ct);
    }

    public Task<RetainedFileDiffView?> GetFileDiffAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string? revisionKey,
        string filePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return this.WithDbAsync(
            async db =>
            {
                var pullRequest = await ResolvePullRequestAsync(db, clientId, repositoryId, pullRequestId, ct);
                if (pullRequest is null)
                {
                    return null;
                }

                var query = db.RetainedFileDiffs
                    .AsNoTracking()
                    .Where(diff => diff.RetainedPullRequestId == pullRequest.Id && diff.FilePath == filePath);

                if (revisionKey is not null)
                {
                    query = query.Where(diff => diff.RevisionKey == revisionKey);
                }

                var diffs = await query
                    .OrderByDescending(diff => diff.CreatedAt)
                    .ToListAsync(ct);

                var match = diffs.FirstOrDefault();
                if (match is null)
                {
                    return null;
                }

                return new RetainedFileDiffView(
                    match.RevisionKey,
                    match.FilePath,
                    match.ChangeType,
                    match.IsBinary,
                    secretProtectionCodec.Unprotect(match.EncryptedUnifiedDiff, FileDiffPurpose),
                    match.CreatedAt);
            },
            ct);
    }

    public Task<IReadOnlyList<RetainedFileSummaryView>> ListRetainedFilesForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        return this.WithDbAsync<IReadOnlyList<RetainedFileSummaryView>>(
            async db =>
            {
                var pullRequest = await ResolvePullRequestAsync(db, clientId, repositoryId, pullRequestId, ct);
                if (pullRequest is null)
                {
                    return [];
                }

                var diffs = await db.RetainedFileDiffs
                    .AsNoTracking()
                    .Where(diff => diff.RetainedPullRequestId == pullRequest.Id)
                    .ToListAsync(ct);

                // Collapse each file path to its newest retained revision; the diff text is never decrypted here.
                return diffs
                    .GroupBy(diff => diff.FilePath, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(diff => diff.CreatedAt).First())
                    .OrderBy(diff => diff.FilePath, StringComparer.Ordinal)
                    .Select(diff => new RetainedFileSummaryView(
                        diff.FilePath,
                        diff.RevisionKey,
                        diff.ChangeType,
                        diff.IsBinary,
                        diff.CreatedAt))
                    .ToList();
            },
            ct);
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var expired = await db.RetainedPullRequests
                    .Where(pr => pr.LastActivityAt < cutoff)
                    .ToListAsync(ct);

                return await RemovePullRequestsAsync(db, expired, ct);
            },
            ct);
    }

    public Task<int> PurgeForConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var forConnection = await db.RetainedPullRequests
                    .Where(pr => pr.ConnectionId == connectionId)
                    .ToListAsync(ct);

                return await RemovePullRequestsAsync(db, forConnection, ct);
            },
            ct);
    }

    public Task<int> PurgeExpiredForConnectionAsync(
        Guid connectionId,
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var expired = await db.RetainedPullRequests
                    .Where(pr => pr.ConnectionId == connectionId && pr.LastActivityAt < cutoff)
                    .ToListAsync(ct);

                return await RemovePullRequestsAsync(db, expired, ct);
            },
            ct);
    }

    public Task<IReadOnlyList<RetainedPullRequestRef>> ListPullRequestRefsForConnectionAsync(
        Guid connectionId,
        DateTimeOffset? cutoff,
        CancellationToken ct = default)
    {
        return this.WithDbAsync<IReadOnlyList<RetainedPullRequestRef>>(
            async db =>
            {
                var query = db.RetainedPullRequests
                    .AsNoTracking()
                    .Where(pr => pr.ConnectionId == connectionId);

                if (cutoff is not null)
                {
                    query = query.Where(pr => pr.LastActivityAt < cutoff.Value);
                }

                return await query
                    .Select(pr => new RetainedPullRequestRef(pr.ClientId, pr.RepositoryId, pr.PullRequestId))
                    .ToListAsync(ct);
            },
            ct);
    }

    private static async Task<int> RemovePullRequestsAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<RetainedPullRequest> pullRequests,
        CancellationToken ct)
    {
        if (pullRequests.Count == 0)
        {
            return 0;
        }

        // Load children so the cascade removes them under providers that do not enforce database-level
        // cascade delete (the in-memory provider used in tests requires the dependents to be tracked).
        var pullRequestIds = pullRequests.Select(pr => pr.Id).ToList();

        var threads = await db.RetainedThreads
            .Where(thread => pullRequestIds.Contains(thread.RetainedPullRequestId))
            .ToListAsync(ct);
        var threadIds = threads.Select(thread => thread.Id).ToList();

        var comments = await db.RetainedThreadComments
            .Where(comment => threadIds.Contains(comment.RetainedThreadId))
            .ToListAsync(ct);

        var diffs = await db.RetainedFileDiffs
            .Where(diff => pullRequestIds.Contains(diff.RetainedPullRequestId))
            .ToListAsync(ct);

        db.RetainedThreadComments.RemoveRange(comments);
        db.RetainedThreads.RemoveRange(threads);
        db.RetainedFileDiffs.RemoveRange(diffs);
        db.RetainedPullRequests.RemoveRange(pullRequests);

        await db.SaveChangesAsync(ct);
        return pullRequests.Count;
    }

    // Resolves the owning retained pull request from its client-scoped identity alone, without a
    // connection id. A repository + pull request is normally retained under a single connection, but if
    // more than one connection retained the same one, the most recently active row is returned so reads
    // surface the freshest retained data.
    private static async Task<RetainedPullRequest?> ResolvePullRequestAsync(
        MeisterProPRDbContext db,
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct)
    {
        return await db.RetainedPullRequests
            .Where(pr => pr.ClientId == clientId
                         && pr.RepositoryId == repositoryId
                         && pr.PullRequestId == pullRequestId)
            .OrderByDescending(pr => pr.LastActivityAt)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<RetainedPullRequest?> FindPullRequestAsync(
        MeisterProPRDbContext db,
        PullRequestRetentionKey key,
        CancellationToken ct)
    {
        return await db.RetainedPullRequests
            .FirstOrDefaultAsync(
                pr => pr.ClientId == key.ClientId
                      && pr.ConnectionId == key.ConnectionId
                      && pr.RepositoryId == key.RepositoryId
                      && pr.PullRequestId == key.PullRequestId,
                ct);
    }

    private static async Task<RetainedPullRequest> GetOrCreatePullRequestAsync(
        MeisterProPRDbContext db,
        PullRequestRetentionKey key,
        string? prState,
        DateTimeOffset? lastActivityAt,
        CancellationToken ct)
    {
        var pullRequest = await FindPullRequestAsync(db, key, ct);
        var now = DateTimeOffset.UtcNow;

        if (pullRequest is null)
        {
            pullRequest = new RetainedPullRequest
            {
                Id = Guid.NewGuid(),
                ClientId = key.ClientId,
                ConnectionId = key.ConnectionId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                PrState = prState ?? string.Empty,
                LastActivityAt = lastActivityAt ?? now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.RetainedPullRequests.Add(pullRequest);
            return pullRequest;
        }

        if (prState is not null)
        {
            pullRequest.PrState = prState;
        }

        if (lastActivityAt is not null)
        {
            pullRequest.LastActivityAt = lastActivityAt.Value;
        }

        pullRequest.UpdatedAt = now;
        return pullRequest;
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    private async Task WithDbAsync(Func<MeisterProPRDbContext, Task> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (contextFactory is null)
        {
            await operation(dbContext);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await operation(db);
    }
}
