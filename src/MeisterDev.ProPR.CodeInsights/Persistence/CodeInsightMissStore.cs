// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.CodeInsights.Ports;
using System.Security.Cryptography;
using System.Text.Json;
using static MeisterDev.ProPR.CodeInsights.Persistence.CodeInsightPullRequestPersistence;
using static MeisterDev.ProPR.CodeInsights.Persistence.CodeInsightProviderScopeLookup;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Persists ordered human-thread eligibility and classifier evidence under one aggregate lock.</summary>
public sealed class CodeInsightMissStore(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightMissStore
{
    private const int MaximumFailedClassifierAttempts = 3;
    private const int MaximumWriteAttempts = 3;
    private const string FindingMessagePurpose = "code-insight-finding-message";
    private const string MissDiscussionPurpose = "code-insight-miss-discussion";

    public Task<CodeInsightThreadEligibilityObservation> ObserveThreadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, bool excludedFromHumanMisses,
        DateTimeOffset observedAt, CancellationToken ct = default) =>
        this.WithMissDbAsync(
            key, async db =>
            {
                var pr = await GetOrCreatePullRequestAsync(db, key, null, observedAt, ct);
                var fingerprint = ThreadEligibilityFingerprint(pr.Id, providerScope, threadId);
                var existing = await db.CodeInsightThreadEligibility.SingleOrDefaultAsync(row => row.IdentityFingerprint == fingerprint, ct);
                var stamp = NormalizeMissStamp(observedAt)!.Value;
                if (existing is null)
                {
                    existing = new CodeInsightThreadEligibility
                    {
                        Id = Guid.CreateVersion7(),
                        CodeInsightPullRequestId = pr.Id,
                        ProviderScope = providerScope,
                        ProviderThreadId = threadId,
                        IdentityFingerprint = fingerprint,
                        SourceObservedAt = stamp,
                        ExcludedFromHumanMisses = excludedFromHumanMisses
                    };
                    db.CodeInsightThreadEligibility.Add(existing);
                }
                else
                {
                    if (db.Database.IsRelational())
                    {
                        await db.Entry(existing).ReloadAsync(ct);
                    }

                    if (stamp > existing.SourceObservedAt)
                    {
                        existing.SourceObservedAt = stamp;
                        existing.ExcludedFromHumanMisses = excludedFromHumanMisses;
                    }
                    else if (stamp == existing.SourceObservedAt && excludedFromHumanMisses)
                    {
                        existing.ExcludedFromHumanMisses = true;
                    }
                }

                await db.SaveChangesAsync(ct);
                return new CodeInsightThreadEligibilityObservation(existing.SourceObservedAt, existing.ExcludedFromHumanMisses);
            }, ct);

    public Task<CodeInsightThreadEligibilityObservation?> GetThreadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, CancellationToken ct = default) =>
        this.WithDbAsync<CodeInsightThreadEligibilityObservation?>(
            async db =>
            {
                var id = await FindPullRequestIdAsync(db, key, ct);
                if (id is null)
                {
                    return null;
                }

                var fingerprint = ThreadEligibilityFingerprint(id.Value, providerScope, threadId);
                return await db.CodeInsightThreadEligibility.AsNoTracking().Where(row => row.IdentityFingerprint == fingerprint)
                    .Select(row => new CodeInsightThreadEligibilityObservation(row.SourceObservedAt, row.ExcludedFromHumanMisses)).SingleOrDefaultAsync(ct);
            }, ct);

    private static string ThreadEligibilityFingerprint(Guid aggregateId, string providerScope, string threadId) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { aggregateId.ToString("D"), providerScope, threadId })));

    private static async Task<CodeInsightThreadEligibility?> FindThreadEligibilityAsync(
        MeisterProPRDbContext db, Guid aggregateId, string providerScope, string threadId, CancellationToken ct)
    {
        var fingerprint = ThreadEligibilityFingerprint(aggregateId, providerScope, threadId);
        return await db.CodeInsightThreadEligibility.AsNoTracking().SingleOrDefaultAsync(row => row.IdentityFingerprint == fingerprint, ct);
    }

    private static bool SupersedesJudgement(CodeInsightThreadEligibility? eligibility, DateTimeOffset? observedAt) =>
        eligibility is not null && (observedAt is null || eligibility.SourceObservedAt > observedAt
                                                       || eligibility.ExcludedFromHumanMisses && eligibility.SourceObservedAt == observedAt);

    public Task<bool> RecordMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(miss);
        miss = miss with { SourceObservedAt = NormalizeMissStamp(miss.SourceObservedAt) };

        return this.WithMissDbAsync(
            key,
            async db =>
            {
                // Create an aggregate for a human miss even when the review produced no findings.
                var pullRequest = await GetOrCreatePullRequestAsync(db, key, null, DateTimeOffset.UtcNow, ct);
                var providerScope = await ResolveMissProviderScopeAsync(db, key.ClientId, miss.ConnectionId, ct, miss.ProviderScope);
                if (SupersedesJudgement(await FindThreadEligibilityAsync(db, pullRequest.Id, providerScope, miss.ProviderThreadId, ct), miss.SourceObservedAt))
                {
                    return false;
                }

                var existing = await db.CodeInsightMisses
                    .FirstOrDefaultAsync(
                        candidate => candidate.CodeInsightPullRequestId == pullRequest.Id
                                     && candidate.ProviderThreadId == miss.ProviderThreadId && candidate.ProviderScope == providerScope,
                        ct);
                if (existing is not null)
                {
                    if (miss.SourceObservedAt is null)
                    {
                        return false;
                    }

                    if (db.Database.IsRelational())
                    {
                        await db.Entry(existing).ReloadAsync(ct);
                    }

                    return await ApplyMissAsync(db, existing, miss, ct);
                }

                var harvestedAt = DateTimeOffset.UtcNow;

                var harvested = new CodeInsightMiss
                {
                    Id = Guid.CreateVersion7(),
                    CodeInsightPullRequestId = pullRequest.Id,
                    ProviderThreadId = miss.ProviderThreadId,
                    TypeMembership = miss.TypeMembership,
                    ProviderScope = providerScope,
                    Qualifier = miss.Qualifier,
                    DimensionClassifierVersion = miss.DimensionClassifierVersion,
                    DimensionConfidence = miss.DimensionConfidence,
                    DimensionJudgementFailed = miss.DimensionJudgementFailed,
                    FailedDimensionAttempts = miss.DimensionJudgementFailed && miss.DimensionModelWasAsked ? 1 : 0,
                    JudgementFailed = miss.JudgementFailed,
                    FailedJudgementAttempts = miss.JudgementFailed && miss.JudgementModelWasAsked ? 1 : 0,
                    SourceFingerprint = miss.SourceFingerprint,
                    SourceObservedAt = miss.SourceObservedAt,
                    FilePath = miss.FilePath,
                    LineNumber = miss.LineNumber,
                    HarvestedAt = harvestedAt,
                };

                harvested.RecordJudgement(
                    miss.IsSubstantive,
                    miss.WasActedOn,
                    miss.IsInScope,
                    miss.Confidence,
                    miss.ClassifierVersion,
                    miss.JudgedThreadResolved,
                    secretProtectionCodec.Protect(miss.Discussion, MissDiscussionPurpose),
                    harvestedAt);

                harvested.RecordOwnFindingExclusion(await HasCurrentOwnFindingAsync(db, harvested, miss, ct), harvestedAt);
                db.CodeInsightMisses.Add(harvested);

                await db.SaveChangesAsync(ct);
                return true;
            },
            ct);
    }

    public Task<CodeInsightMissAcknowledgment> ObserveUnchangedMissAsync(
        CodeInsightPullRequestKey key, string threadId, string fingerprint,
        DateTimeOffset observedAt, CancellationToken ct = default, Guid? connectionId = null,
        string? dimensionClassifierVersion = null, bool excludedAsOwnFinding = false, string? providerScope = null) =>
        this.WithMissDbAsync(
            key, async db =>
            {
                observedAt = NormalizeMissStamp(observedAt)!.Value;
                var id = await FindPullRequestIdAsync(db, key, ct);
                var scope = await ResolveMissProviderScopeAsync(db, key.ClientId, connectionId, ct, providerScope);
                var existing = await db.CodeInsightMisses.FirstOrDefaultAsync(
                    row => row.CodeInsightPullRequestId == id
                           && row.ProviderThreadId == threadId && row.ProviderScope == scope, ct);
                if (existing is null)
                {
                    return new CodeInsightMissAcknowledgment(false, false);
                }

                if (db.Database.IsRelational())
                {
                    await db.Entry(existing).ReloadAsync(ct);
                }

                if (existing.SourceObservedAt > observedAt)
                {
                    return new CodeInsightMissAcknowledgment(true, false);
                }

                var eligibility = await FindThreadEligibilityAsync(db, existing.CodeInsightPullRequestId, scope, threadId, ct);
                if (eligibility is { ExcludedFromHumanMisses: true } && eligibility.SourceObservedAt >= observedAt)
                {
                    excludedAsOwnFinding = true;
                    observedAt = eligibility.SourceObservedAt;
                }
                else if (!excludedAsOwnFinding && eligibility?.SourceObservedAt > observedAt)
                {
                    return new CodeInsightMissAcknowledgment(false, false);
                }

                var completed = HasCompletedObservation(existing, fingerprint, dimensionClassifierVersion);
                if (!excludedAsOwnFinding && !completed)
                {
                    return new CodeInsightMissAcknowledgment(false, false);
                }

                var changed = existing.ExcludedAsOwnFinding != excludedAsOwnFinding;
                existing.RecordOwnFindingExclusion(excludedAsOwnFinding, DateTimeOffset.UtcNow);
                existing.SourceObservedAt = observedAt;
                await db.SaveChangesAsync(ct);
                return new CodeInsightMissAcknowledgment(true, changed);
            }, ct);

    public Task<CodeInsightMissObservation?> GetObservationAsync(
        CodeInsightPullRequestKey key, string threadId, CancellationToken ct = default, Guid? connectionId = null, string? providerScope = null) =>
        this.WithDbAsync<CodeInsightMissObservation?>(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                var candidates = db.CodeInsightMisses.Where(row => row.CodeInsightPullRequestId == aggregateId && row.ProviderThreadId == threadId);
                if (connectionId is not null || providerScope is not null)
                {
                    var scope = await ResolveMissProviderScopeAsync(db, key.ClientId, connectionId, ct, providerScope);
                    candidates = candidates.Where(row => row.ProviderScope == scope);
                }
                else if (await candidates.Select(row => row.ProviderScope).Distinct().Take(2).CountAsync(ct) > 1)
                {
                    return null;
                }

                return await candidates
                    .Select(row => new CodeInsightMissObservation(
                        row.SourceFingerprint, row.JudgementFailed, row.FailedJudgementAttempts,
                        row.IsSubstantive, row.WasActedOn, row.IsInScope, row.ClassifierConfidence, row.JudgedThreadResolved,
                        row.TypeMembership, row.Qualifier, row.DimensionClassifierVersion, row.DimensionConfidence,
                        row.DimensionJudgementFailed, row.FailedDimensionAttempts, row.SourceObservedAt, row.ExcludedAsOwnFinding)).FirstOrDefaultAsync(ct);
            }, ct);

    public Task<bool?> GetJudgedThreadResolvedAsync(
        CodeInsightPullRequestKey key,
        string providerThreadId,
        CancellationToken ct = default,
        Guid? connectionId = null, string? providerScope = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerThreadId);

        return this.WithDbAsync(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return (bool?)null;
                }

                // A missing row and a judgement of an open thread require different retry decisions.
                var candidates = db.CodeInsightMisses.Where(miss =>
                    miss.CodeInsightPullRequestId == aggregateId.Value && miss.ProviderThreadId == providerThreadId);
                if (connectionId is not null || providerScope is not null)
                {
                    var scope = await ResolveMissProviderScopeAsync(db, key.ClientId, connectionId, ct, providerScope);
                    candidates = candidates.Where(row => row.ProviderScope == scope);
                }
                else if (await candidates.Select(row => row.ProviderScope).Distinct().Take(2).CountAsync(ct) > 1)
                {
                    return (bool?)null;
                }

                var judged = await candidates
                    .Select(miss => (bool?)(miss.JudgementFailed ? miss.FailedJudgementAttempts >= MaximumFailedClassifierAttempts : miss.JudgedThreadResolved))
                    .FirstOrDefaultAsync(ct);

                return judged;
            },
            ct);
    }

    public Task<bool> RejudgeMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(miss);
        miss = miss with { SourceObservedAt = NormalizeMissStamp(miss.SourceObservedAt) };

        return this.WithMissDbAsync(
            key,
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return false;
                }

                var providerScope = await ResolveMissProviderScopeAsync(db, key.ClientId, miss.ConnectionId, ct, miss.ProviderScope);
                var existing = await db.CodeInsightMisses
                    .FirstOrDefaultAsync(
                        candidate => candidate.CodeInsightPullRequestId == aggregateId.Value
                                     && candidate.ProviderThreadId == miss.ProviderThreadId && candidate.ProviderScope == providerScope,
                        ct);
                if (existing is null)
                {
                    return false;
                }

                if (db.Database.IsRelational())
                {
                    await db.Entry(existing).ReloadAsync(ct);
                }

                return await ApplyMissAsync(db, existing, miss, ct);
            },
            ct);
    }

    private static DateTimeOffset? NormalizeMissStamp(DateTimeOffset? stamp) => stamp is null ? null : new(stamp.Value.UtcTicks / 10 * 10, TimeSpan.Zero);

    private async Task<bool> ApplyMissAsync(MeisterProPRDbContext db, CodeInsightMiss existing, CodeInsightMissRecord miss, CancellationToken ct)
    {
        if (existing.SourceObservedAt is not null && (miss.SourceObservedAt is null || existing.SourceObservedAt > miss.SourceObservedAt))
        {
            return false;
        }

        if (SupersedesJudgement(
                await FindThreadEligibilityAsync(db, existing.CodeInsightPullRequestId, existing.ProviderScope, miss.ProviderThreadId, ct),
                miss.SourceObservedAt))
        {
            return false;
        }

        var excludedAsOwnFinding = await HasCurrentOwnFindingAsync(db, existing, miss, ct);
        var sameSource = !string.IsNullOrEmpty(miss.SourceFingerprint) && existing.SourceFingerprint == miss.SourceFingerprint;
        if (sameSource && !existing.JudgementFailed && miss.JudgementFailed)
        {
            var exclusionChanged = existing.ExcludedAsOwnFinding != excludedAsOwnFinding;
            existing.RecordOwnFindingExclusion(excludedAsOwnFinding, DateTimeOffset.UtcNow);
            existing.SourceObservedAt = miss.SourceObservedAt ?? existing.SourceObservedAt;
            await db.SaveChangesAsync(ct);
            return exclusionChanged || miss.SourceObservedAt is null;
        }

        var sameDimensionsSource = sameSource && existing.DimensionClassifierVersion == miss.DimensionClassifierVersion;
        var keepDimensions = sameDimensionsSource && !existing.DimensionJudgementFailed && miss.DimensionJudgementFailed;
        var sameHumanJudgement = sameSource && IsHumanJudgementUnchanged(existing, miss);
        var sameDimensions = sameDimensionsSource && AreDimensionsUnchanged(existing, miss);
        if (existing.ExcludedAsOwnFinding == excludedAsOwnFinding && sameHumanJudgement &&
            (sameDimensions && (!existing.DimensionJudgementFailed || existing.FailedDimensionAttempts >= MaximumFailedClassifierAttempts ||
                                !miss.DimensionModelWasAsked) || keepDimensions))
        {
            existing.SourceObservedAt = miss.SourceObservedAt ?? existing.SourceObservedAt;
            await db.SaveChangesAsync(ct);
            return miss.SourceObservedAt is null;
        }

        if (!keepDimensions)
        {
            ApplyDimensions(existing, miss, sameDimensionsSource);
        }

        ApplyHumanJudgement(existing, miss, sameSource, excludedAsOwnFinding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool HasCompletedObservation(CodeInsightMiss existing, string fingerprint, string? dimensionClassifierVersion)
    {
        if (existing.SourceFingerprint != fingerprint)
        {
            return false;
        }

        if (existing.JudgementFailed)
        {
            return existing.FailedJudgementAttempts >= MaximumFailedClassifierAttempts;
        }

        return !existing.IsSubstantive || dimensionClassifierVersion is null
                                       || existing.DimensionClassifierVersion == dimensionClassifierVersion
                                       && (!existing.DimensionJudgementFailed || existing.FailedDimensionAttempts >= MaximumFailedClassifierAttempts);
    }

    private static bool IsHumanJudgementUnchanged(CodeInsightMiss existing, CodeInsightMissRecord miss)
    {
        return !existing.JudgementFailed && !miss.JudgementFailed
                                         && existing.IsSubstantive == miss.IsSubstantive
                                         && existing.WasActedOn == miss.WasActedOn
                                         && existing.IsInScope == miss.IsInScope
                                         && existing.ClassifierConfidence == miss.Confidence
                                         && existing.ClassifierVersion == miss.ClassifierVersion
                                         && existing.JudgedThreadResolved == miss.JudgedThreadResolved;
    }

    private static bool AreDimensionsUnchanged(CodeInsightMiss existing, CodeInsightMissRecord miss)
    {
        return existing.TypeMembership == miss.TypeMembership
               && existing.Qualifier == miss.Qualifier
               && existing.DimensionConfidence == miss.DimensionConfidence
               && existing.DimensionJudgementFailed == miss.DimensionJudgementFailed;
    }

    private static void ApplyDimensions(CodeInsightMiss existing, CodeInsightMissRecord miss, bool sameSource)
    {
        existing.FailedDimensionAttempts = FailedAttempts(
            miss.DimensionJudgementFailed, sameSource, existing.FailedDimensionAttempts, miss.DimensionModelWasAsked);
        existing.DimensionJudgementFailed = miss.DimensionJudgementFailed;
        existing.TypeMembership = miss.TypeMembership;
        existing.Qualifier = miss.Qualifier;
        existing.DimensionClassifierVersion = miss.DimensionClassifierVersion;
        existing.DimensionConfidence = miss.DimensionConfidence;
    }

    private void ApplyHumanJudgement(CodeInsightMiss existing, CodeInsightMissRecord miss, bool sameSource, bool excludedAsOwnFinding)
    {
        existing.JudgementFailed = miss.JudgementFailed;
        existing.FailedJudgementAttempts = FailedAttempts(miss.JudgementFailed, sameSource, existing.FailedJudgementAttempts, miss.JudgementModelWasAsked);
        existing.SourceFingerprint = miss.SourceFingerprint;
        existing.SourceObservedAt = miss.SourceObservedAt ?? existing.SourceObservedAt;
        existing.RecordOwnFindingExclusion(excludedAsOwnFinding, DateTimeOffset.UtcNow);
        existing.RecordJudgement(
            miss.IsSubstantive, miss.WasActedOn, miss.IsInScope, miss.Confidence, miss.ClassifierVersion,
            miss.JudgedThreadResolved, secretProtectionCodec.Protect(miss.Discussion, MissDiscussionPurpose), DateTimeOffset.UtcNow);
    }

    private static int FailedAttempts(bool failed, bool sameSource, int retainedAttempts, bool modelWasAsked)
    {
        return failed ? Math.Min(MaximumFailedClassifierAttempts, (sameSource ? retainedAttempts : 0) + (modelWasAsked ? 1 : 0)) : 0;
    }

    private async Task<bool> HasCurrentOwnFindingAsync(MeisterProPRDbContext db, CodeInsightMiss existing, CodeInsightMissRecord miss, CancellationToken ct)
    {
        var findings = await db.CodeInsightFindings.Where(finding =>
                finding.CodeInsightPullRequestId == existing.CodeInsightPullRequestId && finding.ProviderScope == existing.ProviderScope)
            .Select(finding => new
            {
                finding.ProviderThreadId,
                finding.FilePath,
                finding.LineNumber,
                finding.EncryptedMessage
            }).ToListAsync(ct);
        return findings.Any(finding => string.Equals(finding.ProviderThreadId, miss.ProviderThreadId, StringComparison.Ordinal))
               || HumanFindingOverlap.DuplicatesAnyFinding(
                   miss.FilePath, miss.LineNumber, miss.Discussion,
                   findings.Select(finding => new FindingOverlapCandidate(
                       finding.FilePath, finding.LineNumber, secretProtectionCodec.Unprotect(finding.EncryptedMessage, FindingMessagePurpose))).ToList());
    }

    private async Task<T> WithMissDbAsync<T>(CodeInsightPullRequestKey key, Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await this.WithDbAsync(
                    async db =>
                    {
                        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                        await CodeInsightAggregateWriteLock.AcquireAsync(db, key, ct);

                        var result = await operation(db);
                        if (transaction is not null)
                        {
                            await transaction.CommitAsync(ct);
                        }

                        return result;
                    }, ct);
            }
            catch (DbUpdateException exception) when (attempt < MaximumWriteAttempts - 1 &&
                                                      exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                if (contextFactory is null)
                {
                    dbContext.ChangeTracker.Clear();
                }
            }
        }
    }

    public Task<IReadOnlyList<CodeInsightMissView>> GetMissesForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return this.WithDbAsync<IReadOnlyList<CodeInsightMissView>>(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return [];
                }

                var records = await db.CodeInsightMisses
                    .Where(miss => miss.CodeInsightPullRequestId == aggregateId.Value)
                    .OrderBy(miss => miss.HarvestedAt)
                    .ToListAsync(ct);

                return records
                    .Select(miss => new CodeInsightMissView(
                        miss.Id,
                        miss.ProviderThreadId,
                        miss.FilePath,
                        miss.LineNumber,
                        secretProtectionCodec.Unprotect(miss.EncryptedDiscussion, MissDiscussionPurpose),
                        miss.IsSubstantive,
                        miss.WasActedOn,
                        miss.IsInScope,
                        !miss.JudgementFailed && miss.CountsAsMiss,
                        miss.ClassifierConfidence,
                        miss.ClassifierVersion,
                        miss.HarvestedAt,
                        miss.JudgedThreadResolved,
                        miss.LastJudgedAt, miss.JudgementFailed, miss.ExcludedAsOwnFinding))
                    .ToList();
            },
            ct);
    }

    private async Task WithDbAsync(Func<MeisterProPRDbContext, Task> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        await operation(lease.Context);
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        return await operation(lease.Context);
    }
}
