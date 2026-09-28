// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>
///     Reads the individual findings and harvested threads behind a metric.
/// </summary>
/// <remarks>
///     <para>
///         Scope is resolved through the pull-request aggregate as a subquery rather than a navigation property:
///         the aggregate owns the client, repository, and pull-request identity, and an <c>IN (SELECT …)</c>
///         translates on the database while still evaluating correctly under the in-memory provider the unit
///         tests use.
///     </para>
///     <para>
///         Bounded by construction. A drill-through is a sample somebody is about to read, so the row limit is
///         clamped rather than trusted, and the rows come back newest-review-first because that is the end of the
///         window an operator is asking about.
///     </para>
/// </remarks>
public sealed class CodeInsightBrowseReader(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IPostedCommentComposer postedCommentComposer,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    int maxScannedMisses = CodeInsightBrowseReader.DefaultMaxScannedMisses) : ICodeInsightBrowseReader
{
    private const string FindingMessagePurpose = "code-insight-finding-message";
    private const string MissDiscussionPurpose = "code-insight-miss-discussion";
    private const int MaxLimit = 500;

    /// <summary>
    ///     How many stored misses one page of the eligibility scan reads. Large enough that a run of ProPR's own
    ///     comments does not cost a round trip each, small enough that the common case of a first page that is
    ///     already eligible does not decrypt far more rows than the caller asked for.
    /// </summary>
    private const int PageSize = 200;

    /// <summary>
    ///     The production default for <c>maxScannedMisses</c>, the ceiling on how many stored misses the
    ///     eligibility scan reads before it returns what it has. A window whose newest misses are nearly all
    ///     ProPR's own comments would otherwise walk the whole table to fill a page that cannot be filled, so the
    ///     scan stops here instead. Because of that ceiling, a returned page can be shorter than the caller's
    ///     limit, or empty, even while eligible human threads still exist further back in the window: the result
    ///     carries no truncation marker, so a caller distinguishes "no more misses" from "the scan hit its
    ///     ceiling" only by paging again and checking whether another page still has rows. The ceiling is a
    ///     constructor parameter precisely so a test can set it low enough to reach without seeding thousands of
    ///     rows.
    /// </summary>
    private const int DefaultMaxScannedMisses = 5_000;

    public Task<IReadOnlyList<CodeInsightFindingRow>> ListFindingsAsync(
        CodeInsightBrowseQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return this.WithDbAsync<IReadOnlyList<CodeInsightFindingRow>>(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return [];
                }

                var scopes = await LoadScopesAsync(db, query, ct);
                if (scopes.Count == 0)
                {
                    return [];
                }

                var (from, toExclusive) = Window(query);
                var aggregateIds = scopes.Keys.ToList();

                var findings = db.CodeInsightFindings
                    .AsNoTracking()
                    .Where(finding => aggregateIds.Contains(finding.CodeInsightPullRequestId))
                    .Where(finding => finding.ObservedAt >= from && finding.ObservedAt < toExclusive);

                if (query.FilePath is not null)
                {
                    findings = findings.Where(finding => finding.FilePath == query.FilePath);
                }

                // Exact, not prefix: the drill from a symbol hotspot must show that definition's findings and no
                // sibling's, and that makes the number on the row checkable.
                if (query.SymbolName is not null)
                {
                    findings = findings.Where(finding => finding.OriginSymbolName == query.SymbolName);
                }

                if (query.CoreType is not null)
                {
                    var slug = query.CoreType;
                    findings = findings.Where(finding => db.CodeInsightFindingTags
                        .Any(tag => tag.CodeInsightFindingId == finding.Id && tag.IsCore && tag.CoreSlug == slug));
                }

                if (query.Disposition is not null)
                {
                    var disposition = query.Disposition.Value;
                    findings = findings.Where(finding => db.CodeInsightFindingDispositions
                        .Any(outcome => outcome.CodeInsightFindingId == finding.Id
                                        && outcome.Disposition == disposition));
                }

                if (query.RejectionReason is not null)
                {
                    var reason = query.RejectionReason.Value;
                    findings = findings.Where(finding => db.CodeInsightFindingDispositions
                        .Any(outcome => outcome.CodeInsightFindingId == finding.Id
                                        && outcome.RejectionReason == reason));
                }

                var rows = await findings
                    .OrderByDescending(finding => finding.ObservedAt)
                    // A stable tie-break, so paging or a repeated read does not reshuffle equal timestamps.
                    .ThenBy(finding => finding.Id)
                    .Take(Limit(query))
                    .Select(finding => new
                    {
                        finding.Id,
                        finding.CodeInsightPullRequestId,
                        finding.JobId,
                        finding.FilePath,
                        finding.LineNumber,
                        finding.Severity,
                        finding.EncryptedMessage,
                        finding.ProviderThreadId,
                        finding.ObservedAt,
                    })
                    .ToListAsync(ct);

                if (rows.Count == 0)
                {
                    return [];
                }

                var findingIds = rows.Select(row => row.Id).ToList();

                var tags = await db.CodeInsightFindingTags
                    .AsNoTracking()
                    .Where(tag => findingIds.Contains(tag.CodeInsightFindingId) && tag.IsCore && tag.CoreSlug != null)
                    .Select(tag => new { tag.CodeInsightFindingId, Slug = tag.CoreSlug! })
                    .ToListAsync(ct);

                var outcomes = await db.CodeInsightFindingDispositions
                    .AsNoTracking()
                    .Where(outcome => findingIds.Contains(outcome.CodeInsightFindingId))
                    .Select(outcome => new
                    {
                        outcome.CodeInsightFindingId,
                        outcome.Disposition,
                        outcome.RejectionReason,
                    })
                    .ToListAsync(ct);

                var tagsByFinding = tags
                    .GroupBy(tag => tag.CodeInsightFindingId)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<string>)group
                            .Select(tag => tag.Slug)
                            .OrderBy(slug => slug, StringComparer.Ordinal)
                            .ToList());

                var outcomeByFinding = outcomes
                    .ToDictionary(outcome => outcome.CodeInsightFindingId, outcome => outcome);

                return rows
                    .Select(row =>
                    {
                        var scope = scopes[row.CodeInsightPullRequestId];
                        var hasOutcome = outcomeByFinding.TryGetValue(row.Id, out var outcome);
                        return new CodeInsightFindingRow(
                            row.Id,
                            scope.ClientId,
                            scope.RepositoryId,
                            scope.PullRequestId,
                            row.JobId,
                            row.FilePath,
                            row.LineNumber,
                            row.Severity,
                            secretProtectionCodec.Unprotect(row.EncryptedMessage, FindingMessagePurpose),
                            tagsByFinding.TryGetValue(row.Id, out var slugs) ? slugs : [],
                            hasOutcome ? outcome!.Disposition : null,
                            row.ProviderThreadId,
                            row.ObservedAt,
                            hasOutcome ? outcome!.RejectionReason : null);
                    })
                    .ToList();
            },
            ct);
    }

    public Task<IReadOnlyList<CodeInsightMissRow>> ListMissesAsync(
        CodeInsightBrowseQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return this.WithDbAsync<IReadOnlyList<CodeInsightMissRow>>(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return [];
                }

                var scopes = await LoadScopesAsync(db, query, ct);
                if (scopes.Count == 0)
                {
                    return [];
                }

                var (from, toExclusive) = Window(query);
                var aggregateIds = scopes.Keys.ToList();

                var misses = db.CodeInsightMisses
                    .AsNoTracking()
                    .Where(miss => aggregateIds.Contains(miss.CodeInsightPullRequestId))
                    .Where(miss => miss.HarvestedAt >= from && miss.HarvestedAt < toExclusive);

                if (query.FilePath is not null)
                {
                    misses = misses.Where(miss => miss.FilePath == query.FilePath);
                }

                var limit = Limit(query);
                var eligible = new List<CodeInsightMissRow>(limit);
                var read = 0;

                // Keyset paging on the same (HarvestedAt, Id) order the page is read in: each page's WHERE
                // carries the previous page's last row forward, instead of an OFFSET that would make every later
                // page walk further into rows the database has already discarded.
                DateTimeOffset? cursorHarvestedAt = null;
                Guid? cursorId = null;

                // Rows harvested under older rules are already stored, and nothing re-judges them: they are read
                // back as they were written. A list that presents ProPR's own summary as a thread a person opened
                // is wrong on its face, whatever the recall number beside it does, so those rows are dropped. The
                // records stay, because deleting evidence is a separate decision.
                //
                // The discussion is stored encrypted, so the database cannot decide eligibility and the pages
                // have to be read and decrypted here. Reading only as far as the requested number of eligible
                // rows keeps a page of ProPR's own comments from consuming the caller's limit and returning less
                // than was asked for, or nothing at all.
                while (eligible.Count < limit && read < maxScannedMisses)
                {
                    var pageQuery = misses;
                    if (cursorHarvestedAt is { } afterHarvestedAt && cursorId is { } afterId)
                    {
                        pageQuery = pageQuery.Where(miss =>
                            miss.HarvestedAt < afterHarvestedAt
                            || (miss.HarvestedAt == afterHarvestedAt && miss.Id > afterId));
                    }

                    var page = await pageQuery
                        .OrderByDescending(miss => miss.HarvestedAt)
                        .ThenBy(miss => miss.Id)
                        .Take(Math.Min(PageSize, maxScannedMisses - read))
                        .ToListAsync(ct);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    read += page.Count;
                    var lastOfPage = page[^1];
                    cursorHarvestedAt = lastOfPage.HarvestedAt;
                    cursorId = lastOfPage.Id;

                    foreach (var miss in page)
                    {
                        var discussion = secretProtectionCodec.Unprotect(miss.EncryptedDiscussion, MissDiscussionPurpose);
                        if (!HarvestedThreadEligibility.IsHumanThread(discussion, postedCommentComposer.Text))
                        {
                            continue;
                        }

                        var scope = scopes[miss.CodeInsightPullRequestId];
                        eligible.Add(
                            new CodeInsightMissRow(
                                miss.Id,
                                scope.ClientId,
                                scope.RepositoryId,
                                scope.PullRequestId,
                                miss.ProviderThreadId,
                                miss.FilePath,
                                miss.LineNumber,
                                discussion,
                                miss.IsSubstantive,
                                miss.WasActedOn,
                                miss.IsInScope,
                                miss.CountsAsMiss,
                                miss.ClassifierConfidence,
                                miss.HarvestedAt));

                        if (eligible.Count == limit)
                        {
                            break;
                        }
                    }
                }

                return eligible;
            },
            ct);
    }

    /// <summary>
    ///     Resolves the pull-request aggregates inside the query's scope. The client filter is applied
    ///     unconditionally: this is the one place tenancy is decided for a drill-through, so it does not depend
    ///     on any later filter being present.
    /// </summary>
    private static async Task<Dictionary<Guid, ScopeRow>> LoadScopesAsync(
        MeisterProPRDbContext db,
        CodeInsightBrowseQuery query,
        CancellationToken ct)
    {
        var clientIds = query.ClientIds.ToList();
        var aggregates = db.CodeInsightPullRequests
            .AsNoTracking()
            .Where(pullRequest => clientIds.Contains(pullRequest.ClientId));

        if (query.RepositoryId is not null)
        {
            aggregates = aggregates.Where(pullRequest => pullRequest.RepositoryId == query.RepositoryId);
        }

        if (query.PullRequestId is not null)
        {
            aggregates = aggregates.Where(pullRequest => pullRequest.PullRequestId == query.PullRequestId.Value);
        }

        var rows = await aggregates
            .Select(pullRequest => new
            {
                pullRequest.Id,
                pullRequest.ClientId,
                pullRequest.RepositoryId,
                pullRequest.PullRequestId,
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            row => row.Id,
            row => new ScopeRow(row.ClientId, row.RepositoryId, row.PullRequestId));
    }

    /// <summary>
    ///     Turns the inclusive date window into a half-open instant range, so a record stamped late on the last
    ///     day of the window is inside it.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset ToExclusive) Window(CodeInsightBrowseQuery query)
    {
        return (
            new DateTimeOffset(query.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(query.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    private static int Limit(CodeInsightBrowseQuery query)
    {
        return Math.Clamp(query.Limit, 1, MaxLimit);
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    private readonly record struct ScopeRow(Guid ClientId, string RepositoryId, long PullRequestId);
}
