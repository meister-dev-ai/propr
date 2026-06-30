// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;

/// <summary>
///     Database-backed store for posted-comment provenance. Rows map a provider-native comment back to
///     the review job that posted it; all columns are structured metadata persisted as plaintext so they
///     remain queryable. The store is independent of the review and memory tables.
/// </summary>
/// <remarks>
///     Every operation runs on a fresh context created from the injected
///     <see cref="IDbContextFactory{TContext}" /> when one is available, falling back to the injected
///     request-scoped context when it is null (so tests can pass a single context). This isolation is
///     deliberate: provenance is a best-effort archival write, and isolating it means a failure here can
///     never leave tracked entities behind that poison the shared request-scoped context and break the
///     subsequent review/crawl saves.
/// </remarks>
public sealed class PostedCommentOriginStore(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IPostedCommentOriginStore
{
    public Task RecordAsync(IReadOnlyList<PostedCommentOriginEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        return this.WithDbAsync(
            async db =>
            {
                // Dedupe the incoming batch by the full natural key first, then track keys already added in
                // this call. Azure DevOps comment ids are thread-local, so one publish that creates several
                // threads can yield several entries that share a provider comment id but differ by thread —
                // those are distinct rows. Two entries with the same full key would collide on the unique
                // constraint, and the per-entry DB existence check below cannot see an unsaved same-batch Add,
                // so we must guard against both an intra-batch duplicate key and a re-insert of one row.
                var seenInBatch = new HashSet<NaturalKey>();

                foreach (var entry in entries)
                {
                    var key = new NaturalKey(
                        entry.ClientId,
                        entry.RepositoryId,
                        entry.PullRequestId,
                        entry.ProviderThreadId,
                        entry.ProviderCommentId);
                    if (!seenInBatch.Add(key))
                    {
                        continue;
                    }

                    var existing = await db.PostedCommentOrigins
                        .FirstOrDefaultAsync(
                            origin => origin.ClientId == entry.ClientId
                                      && origin.RepositoryId == entry.RepositoryId
                                      && origin.PullRequestId == entry.PullRequestId
                                      && origin.ProviderThreadId == entry.ProviderThreadId
                                      && origin.ProviderCommentId == entry.ProviderCommentId,
                            ct);

                    if (existing is null)
                    {
                        db.PostedCommentOrigins.Add(
                            new PostedCommentOrigin
                            {
                                Id = Guid.NewGuid(),
                                ClientId = entry.ClientId,
                                RepositoryId = entry.RepositoryId,
                                PullRequestId = entry.PullRequestId,
                                ProviderThreadId = entry.ProviderThreadId,
                                ProviderCommentId = entry.ProviderCommentId,
                                JobId = entry.JobId,
                                PostedAt = entry.PostedAt,
                            });
                        continue;
                    }

                    // Refresh the provenance of an already-recorded comment to the latest posting.
                    existing.JobId = entry.JobId;
                    existing.PostedAt = entry.PostedAt;
                }

                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task<Guid?> GetJobIdForCommentAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string? providerThreadId,
        string providerCommentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCommentId);

        return this.WithDbAsync(
            async db =>
            {
                // Comment-id-primary resolution: a comment id is globally unique within a pull request for
                // most providers (GitHub/GitLab/Forgejo), so the comment id alone resolves it and the thread
                // id is ignored — the crawler may report a different thread id (or none) than was recorded
                // at publish time. Only Azure DevOps scopes comment ids to a thread, so several origins can
                // share one comment id; the thread id breaks that collision.
                var candidates = await db.PostedCommentOrigins
                    .AsNoTracking()
                    .Where(origin => origin.ClientId == clientId
                                     && origin.RepositoryId == repositoryId
                                     && origin.PullRequestId == pullRequestId
                                     && origin.ProviderCommentId == providerCommentId)
                    .Select(origin => new { origin.ProviderThreadId, origin.JobId })
                    .ToListAsync(ct);

                if (candidates.Count == 0)
                {
                    return null;
                }

                if (candidates.Count == 1)
                {
                    return candidates[0].JobId;
                }

                var disambiguated = candidates
                    .FirstOrDefault(candidate => candidate.ProviderThreadId == providerThreadId);
                return disambiguated is null ? (Guid?)null : disambiguated.JobId;
            },
            ct);
    }

    public Task<IReadOnlyList<PostedCommentOriginRow>> GetJobIdsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        return this.WithDbAsync<IReadOnlyList<PostedCommentOriginRow>>(
            async db =>
            {
                var rows = await db.PostedCommentOrigins
                    .AsNoTracking()
                    .Where(origin => origin.ClientId == clientId
                                     && origin.RepositoryId == repositoryId
                                     && origin.PullRequestId == pullRequestId)
                    .Select(origin => new PostedCommentOriginRow(
                        origin.ProviderThreadId,
                        origin.ProviderCommentId,
                        origin.JobId))
                    .ToListAsync(ct);

                return rows;
            },
            ct);
    }

    public Task<int> PurgeForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        return this.WithDbAsync(
            async db =>
            {
                var rows = await db.PostedCommentOrigins
                    .Where(origin => origin.ClientId == clientId
                                     && origin.RepositoryId == repositoryId
                                     && origin.PullRequestId == pullRequestId)
                    .ToListAsync(ct);

                return await RemoveRowsAsync(db, rows, ct);
            },
            ct);
    }

    public Task<int> PurgeForPullRequestsAsync(
        IReadOnlyList<PostedCommentOriginPullRequestRef> pullRequests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pullRequests);

        if (pullRequests.Count == 0)
        {
            return Task.FromResult(0);
        }

        return this.WithDbAsync(
            async db =>
            {
                var removedTotal = 0;
                foreach (var pullRequest in pullRequests)
                {
                    var rows = await db.PostedCommentOrigins
                        .Where(origin => origin.ClientId == pullRequest.ClientId
                                         && origin.RepositoryId == pullRequest.RepositoryId
                                         && origin.PullRequestId == pullRequest.PullRequestId)
                        .ToListAsync(ct);

                    removedTotal += await RemoveRowsAsync(db, rows, ct);
                }

                return removedTotal;
            },
            ct);
    }

    private static async Task<int> RemoveRowsAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<PostedCommentOrigin> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        db.PostedCommentOrigins.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
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

    // The full provenance natural key, used to dedupe a single RecordAsync batch before it reaches the
    // database so an intra-batch duplicate can never collide on the unique constraint.
    private readonly record struct NaturalKey(
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        string? ProviderThreadId,
        string ProviderCommentId);
}
