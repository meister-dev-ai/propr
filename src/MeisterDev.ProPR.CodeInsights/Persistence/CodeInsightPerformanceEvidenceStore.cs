// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Stores live metadata without changing the initial disposition or a sealed metric.</summary>
public sealed class CodeInsightPerformanceEvidenceStore(MeisterProPRDbContext dbContext, IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
    : ICodeInsightPerformanceEvidenceStore
{
    private const int MaximumNativeStatusCharacters = 64;

    /// <inheritdoc />
    public async Task<bool> ObserveUnchangedOutcomeAsync(
        Guid findingId, string nativeStatus, string sourceFingerprint, DateTimeOffset observedAt, CancellationToken ct = default,
        CodeInsightPublicationReceipt? receipt = null)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null)
        {
            return false;
        }

        if (!CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt))
        {
            return true;
        }

        if (finding.OutcomeObservedAt > observedAt)
        {
            return true;
        }

        if (!CodeInsightCurrentOutcomePolicy.MatchesCompleted(finding, nativeStatus, sourceFingerprint))
        {
            return false;
        }

        foreach (var publication in await LoadPublicationsAsync(db, finding, ct))
        {
            if (db.Database.IsRelational())
            {
                await db.Entry(publication).ReloadAsync(ct);
            }

            if (CodeInsightCurrentOutcomePolicy.MatchesCompleted(publication, nativeStatus, sourceFingerprint) &&
                (publication.OutcomeObservedAt is null || publication.OutcomeObservedAt < observedAt))
            {
                publication.OutcomeObservedAt = observedAt;
            }
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RecordCurrentOutcomeAsync(
        Guid findingId, string nativeStatus, CodeInsightDispositionRecord? outcome, DateTimeOffset observedAt, CancellationToken ct = default,
        string? sourceFingerprint = null, CodeInsightPublicationReceipt? receipt = null)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var finding = await LoadLockedFindingAsync(db, findingId, ct);
        if (finding is null || !CodeInsightCurrentOutcomePolicy.MatchesReceipt(finding, receipt) || finding.OutcomeObservedAt > observedAt ||
            nativeStatus.Length > MaximumNativeStatusCharacters)
        {
            return false;
        }

        var publications = await LoadPublicationsAsync(db, finding, ct);
        var unchanged = CodeInsightCurrentOutcomePolicy.MatchesOutcome(finding, nativeStatus, outcome, sourceFingerprint);
        foreach (var publication in publications.Where(row => row.OutcomeObservedAt is null || row.OutcomeObservedAt <= observedAt))
        {
            if (db.Database.IsRelational())
            {
                await db.Entry(publication).ReloadAsync(ct);
            }

            if (publication.OutcomeObservedAt > observedAt)
            {
                continue;
            }

            if (unchanged)
            {
                if (CodeInsightCurrentOutcomePolicy.MatchesOutcome(publication, nativeStatus, outcome, sourceFingerprint) &&
                    publication.OutcomeObservedAt != observedAt)
                {
                    publication.OutcomeObservedAt = observedAt;
                }

                continue;
            }

            CodeInsightCurrentOutcomePolicy.Apply(publication, nativeStatus, outcome, observedAt, sourceFingerprint);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return !unchanged;
    }

    private static async Task<CodeInsightFinding?> LoadLockedFindingAsync(MeisterProPRDbContext db, Guid findingId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var identity = await db.CodeInsightFindings.AsNoTracking().Where(row => row.Id == findingId)
                .Select(row => new
                {
                    row.CodeInsightPullRequestId,
                    row.ProviderScope,
                    row.ProviderThreadId,
                    row.ProviderCommentId
                }).FirstOrDefaultAsync(ct);
            if (identity is null)
            {
                return null;
            }

            // Lock the complete receipt family in a stable order before checking evidence and observedAt.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM code_insight_findings WHERE id = {findingId} OR (code_insight_pull_request_id = {identity.CodeInsightPullRequestId} AND \"ProviderScope\" = {identity.ProviderScope} AND provider_thread_id = {identity.ProviderThreadId} AND provider_comment_id IS NOT DISTINCT FROM {identity.ProviderCommentId}) ORDER BY id FOR UPDATE",
                ct);
        }

        var finding = await db.CodeInsightFindings.FirstOrDefaultAsync(row => row.Id == findingId, ct);
        if (finding is not null && db.Database.IsRelational())
        {
            await db.Entry(finding).ReloadAsync(ct);
        }

        return finding;
    }

    private static async Task<List<CodeInsightFinding>> LoadPublicationsAsync(MeisterProPRDbContext db, CodeInsightFinding finding, CancellationToken ct) =>
        finding.ProviderThreadId is null
            ? [finding]
            : await db.CodeInsightFindings.Where(row =>
                row.CodeInsightPullRequestId == finding.CodeInsightPullRequestId && row.ProviderScope == finding.ProviderScope
                                                                                 && row.ProviderThreadId == finding.ProviderThreadId &&
                                                                                 row.ProviderCommentId == finding.ProviderCommentId).ToListAsync(ct);
}
