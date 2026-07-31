// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Survival;
using MeisterDev.ProPR.CodeInsights.Taxonomy;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>
///     Database-backed store for durable code-insight finding records. Finding text is encrypted at rest
///     via <see cref="ISecretProtectionCodec" />; all structured metadata is persisted as plaintext so it
///     remains queryable.
/// </summary>
/// <remarks>
///     Every operation runs on a fresh context created from the injected
///     <see cref="IDbContextFactory{TContext}" /> when one is available, falling back to the injected
///     request-scoped context when it is null (so tests can pass a single context). Collection is a
///     best-effort side-write; isolating it means a failure here can never leave tracked entities behind
///     that poison the shared request-scoped context and break the subsequent review/crawl saves.
///     <para>
///         One class serves five narrow boundaries: findings, classification, outcomes, harvested threads and
///         retention. Consumers depend on the one they use, so a test double stubs two or three methods rather than
///         sixteen and a new kind of collected record gets its own port. The implementations stay together because
///         they share the same context handling, the same codec purposes and the same aggregate lookup, and
///         splitting them would either duplicate that or hide it behind inheritance.
///     </para>
/// </remarks>
public sealed class CodeInsightFindingStore(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
    : ICodeInsightFindingStore,
        ICodeInsightClassificationStore,
        ICodeInsightDispositionStore,
        ICodeInsightMissStore,
        ICodeInsightRetentionStore
{
    private const string FindingMessagePurpose = "code-insight-finding-message";
    private const string MissDiscussionPurpose = "code-insight-miss-discussion";

    public Task TouchPullRequestAsync(
        CodeInsightPullRequestKey key,
        string pullRequestState,
        DateTimeOffset lastActivityAt,
        string? repositoryName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return this.WithDbAsync(
            async db =>
            {
                await GetOrCreatePullRequestAsync(db, key, pullRequestState, lastActivityAt, ct, repositoryName);
                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task<int> MaterialiseFindingsAsync(
        CodeInsightPullRequestKey key,
        Guid jobId,
        string revisionKey,
        DateTimeOffset observedAt,
        IReadOnlyList<CodeInsightFindingSnapshot> findings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionKey);
        ArgumentNullException.ThrowIfNull(findings);

        if (findings.Count == 0)
        {
            return Task.FromResult(0);
        }

        return this.WithDbAsync(
            async db =>
            {
                var pullRequest = await GetOrCreatePullRequestAsync(db, key, null, observedAt, ct);

                // Load the increment's already-materialised records once and match on the natural key, so a
                // re-processed event refreshes them in place. Recreating them would hand out new surrogate
                // identifiers and orphan every tag, disposition, and memory link already pointing at the old
                // ones.
                var existing = await db.CodeInsightFindings
                    .Where(candidate => candidate.CodeInsightPullRequestId == pullRequest.Id
                                        && candidate.RevisionKey == revisionKey)
                    .ToDictionaryAsync(candidate => candidate.Ordinal, ct);

                // The previous increment's findings, with the chain each belongs to. A finding that restates one
                // of them continues its chain; anything else starts a new one. This is what turns per-increment
                // rows into an answer to "was it still being raised when the pull request finished".
                var earlier = await LoadPreviousIncrementAsync(db, pullRequest.Id, revisionKey, ct);
                var continuedChains = new HashSet<Guid>();

                var now = DateTimeOffset.UtcNow;
                var created = 0;

                foreach (var finding in findings)
                {
                    var encryptedMessage = secretProtectionCodec.Protect(finding.Message, FindingMessagePurpose);

                    if (existing.TryGetValue(finding.Ordinal, out var current))
                    {
                        // The provider identifiers are the only fields a re-post can legitimately fill in;
                        // the rest of the record is fixed by the increment that produced it. Never overwrite a
                        // known id with null: a later pass that posted nothing must not erase the join key a
                        // disposition consumer depends on.
                        current.ProviderThreadId = finding.ProviderThreadId ?? current.ProviderThreadId;
                        current.ProviderCommentId = finding.ProviderCommentId ?? current.ProviderCommentId;
                        continue;
                    }

                    var continuedChain = FindingChainMatcher.FindContinuedChain(
                        finding.FilePath,
                        finding.Message,
                        earlier,
                        continuedChains);

                    if (continuedChain is not null)
                    {
                        continuedChains.Add(continuedChain.Value);
                    }

                    db.CodeInsightFindings.Add(
                        new CodeInsightFinding
                        {
                            Id = Guid.CreateVersion7(),
                            CodeInsightPullRequestId = pullRequest.Id,
                            JobId = jobId,
                            RevisionKey = revisionKey,
                            FindingChainId = continuedChain ?? Guid.CreateVersion7(),
                            Ordinal = finding.Ordinal,
                            FilePath = finding.FilePath,
                            LineNumber = finding.LineNumber,
                            Severity = finding.Severity,
                            EncryptedMessage = encryptedMessage,
                            OriginPassKind = finding.OriginPassKind,
                            OriginPassIndex = finding.OriginPassIndex,
                            OriginPassLens = finding.OriginPassLens,
                            OriginPassShadow = finding.OriginPassShadow,
                            OriginModelId = finding.OriginModelId,
                            OriginLogicalModelName = finding.OriginLogicalModelName,
                            OriginSymbolName = finding.OriginSymbolName,
                            OriginSymbolKind = finding.OriginSymbolKind,
                            ScopeRelation = finding.ScopeRelation,
                            SourceReadGrounding = finding.SourceReadGrounding,
                            ProviderThreadId = finding.ProviderThreadId,
                            ProviderCommentId = finding.ProviderCommentId,
                            ObservedAt = observedAt,
                            CreatedAt = now,
                        });
                    created++;
                }

                await db.SaveChangesAsync(ct);

                // The newest increment collected, which is what a chain's fate is judged against. Derived from the
                // rows rather than assumed to be the one just written: increments can be re-processed out of
                // order, and letting an older one claim to be newest would make every persisting chain look
                // abandoned.
                pullRequest.LatestRevisionKey = await ResolveLatestRevisionKeyAsync(db, pullRequest.Id, ct);
                await db.SaveChangesAsync(ct);

                return created;
            },
            ct);
    }

    /// <summary>
    ///     Loads the findings of the increment immediately before <paramref name="revisionKey" />, decrypted, with
    ///     the chain each already belongs to.
    /// </summary>
    /// <remarks>
    ///     Only the previous increment, not the whole history. A problem that was raised, disappeared for an
    ///     increment, and came back is a new chain, which is the honest reading: the reviewer stopped reporting it
    ///     and started again, and treating that as one unbroken chain would hide exactly the flakiness this
    ///     measurement exists to show.
    /// </remarks>
    private async Task<List<FindingChainCandidate>> LoadPreviousIncrementAsync(
        MeisterProPRDbContext db,
        Guid aggregateId,
        string revisionKey,
        CancellationToken ct)
    {
        var previousKey = await db.CodeInsightFindings
            .Where(finding => finding.CodeInsightPullRequestId == aggregateId && finding.RevisionKey != revisionKey)
            .OrderByDescending(finding => finding.ObservedAt)
            .Select(finding => finding.RevisionKey)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(previousKey))
        {
            return [];
        }

        var rows = await db.CodeInsightFindings
            .Where(finding => finding.CodeInsightPullRequestId == aggregateId && finding.RevisionKey == previousKey)
            .Select(finding => new
            {
                finding.FindingChainId,
                finding.FilePath,
                finding.EncryptedMessage,
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new FindingChainCandidate(
                row.FindingChainId,
                row.FilePath,
                secretProtectionCodec.Unprotect(row.EncryptedMessage, FindingMessagePurpose)))
            .ToList();
    }

    /// <summary>
    ///     Returns the revision key of the pull request's newest collected increment.
    /// </summary>
    /// <remarks>
    ///     A revision key is opaque (an iteration number, a commit sha, or a patch identity depending on the
    ///     provider) so it cannot be ordered by value. Observation time is the one ordering that holds across all
    ///     of them, and reading it back from the rows makes the answer independent of the order increments arrived
    ///     in.
    /// </remarks>
    private static async Task<string> ResolveLatestRevisionKeyAsync(
        MeisterProPRDbContext db,
        Guid aggregateId,
        CancellationToken ct)
    {
        var latest = await db.CodeInsightFindings
            .Where(finding => finding.CodeInsightPullRequestId == aggregateId)
            .OrderByDescending(finding => finding.ObservedAt)
            .Select(finding => finding.RevisionKey)
            .FirstOrDefaultAsync(ct);

        return latest ?? string.Empty;
    }

    public Task<IReadOnlyList<CodeInsightFindingView>> GetFindingsForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return this.WithDbAsync<IReadOnlyList<CodeInsightFindingView>>(
            async db =>
            {
                var pullRequestId = await FindPullRequestIdAsync(db, key, ct);
                if (pullRequestId is null)
                {
                    return [];
                }

                var records = await db.CodeInsightFindings
                    .Where(finding => finding.CodeInsightPullRequestId == pullRequestId.Value)
                    .OrderBy(finding => finding.RevisionKey)
                    .ThenBy(finding => finding.Ordinal)
                    .ToListAsync(ct);

                return records.Select(this.ToView).ToList();
            },
            ct);
    }

    public Task<IReadOnlyList<CodeInsightFindingClassificationView>> GetClassificationsForJobAsync(
        Guid jobId,
        int maxClassificationAttempts,
        CancellationToken ct = default)
    {
        return this.WithDbAsync<IReadOnlyList<CodeInsightFindingClassificationView>>(
            async db =>
            {
                // Served off ix_code_insight_findings_job_id. Only the columns the view needs are projected,
                // the encrypted message is deliberately not among them, so this read never decrypts anything.
                var findings = await db.CodeInsightFindings
                    .Where(finding => finding.JobId == jobId)
                    .OrderBy(finding => finding.Ordinal)
                    .Select(finding => new
                    {
                        finding.Id,
                        finding.Ordinal,
                        finding.Level,
                        finding.Qualifier,
                        finding.ClassifiedAt,
                        finding.ClassificationAttempts,
                        finding.ClassificationConfidence,
                    })
                    .ToListAsync(ct);

                if (findings.Count == 0)
                {
                    return [];
                }

                var findingIds = findings.Select(finding => finding.Id).ToList();

                // Custom assignments resolve to a slug for display. A retired tag still resolves, which is the
                // whole reason retirement is a timestamp rather than a delete.
                var assignments = await db.CodeInsightFindingTags
                    .Where(tag => findingIds.Contains(tag.CodeInsightFindingId))
                    .Select(tag => new
                    {
                        tag.CodeInsightFindingId,
                        tag.IsCore,
                        tag.CoreSlug,
                        CustomSlug = tag.CustomTag == null ? null : tag.CustomTag.Slug,
                    })
                    .ToListAsync(ct);

                var byFinding = assignments
                    .GroupBy(tag => tag.CodeInsightFindingId)
                    .ToDictionary(group => group.Key, group => group.ToList());

                return findings
                    .Select(finding =>
                    {
                        byFinding.TryGetValue(finding.Id, out var tags);
                        tags ??= [];

                        return new CodeInsightFindingClassificationView(
                            finding.Ordinal,
                            ResolveStatus(finding.ClassifiedAt, finding.ClassificationAttempts, maxClassificationAttempts),
                            tags.Where(tag => tag.IsCore && tag.CoreSlug is not null)
                                .Select(tag => tag.CoreSlug!)
                                .OrderBy(slug => slug, StringComparer.Ordinal)
                                .ToList(),
                            tags.Where(tag => !tag.IsCore && tag.CustomSlug is not null)
                                .Select(tag => tag.CustomSlug!)
                                .OrderBy(slug => slug, StringComparer.Ordinal)
                                .ToList(),
                            finding.Level,
                            finding.Qualifier,
                            finding.ClassificationConfidence);
                    })
                    .ToList();
            },
            ct);
    }

    private static CodeInsightClassificationStatus ResolveStatus(
        DateTimeOffset? classifiedAt,
        int attempts,
        int maxAttempts)
    {
        if (classifiedAt is not null)
        {
            return CodeInsightClassificationStatus.Classified;
        }

        // Still retryable means "not yet", which a caller must be able to tell apart from "the model could not
        // place this": otherwise a freshly finished review looks like one with nothing to say about it.
        return attempts < maxAttempts
            ? CodeInsightClassificationStatus.Pending
            : CodeInsightClassificationStatus.Unclassifiable;
    }

    public Task<CodeInsightFindingView?> FindByProviderThreadAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string providerThreadId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerThreadId);

        return this.WithDbAsync(
            async db =>
            {
                var key = new CodeInsightPullRequestKey(clientId, repositoryId, pullRequestId);
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return null;
                }

                var record = await db.CodeInsightFindings
                    .Where(finding => finding.CodeInsightPullRequestId == aggregateId.Value
                                      && finding.ProviderThreadId == providerThreadId)
                    // A thread can only belong to one finding, but ordering keeps the result deterministic
                    // if historical data ever violates that.
                    .OrderBy(finding => finding.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                return record is null ? null : this.ToView(record);
            },
            ct);
    }

    public Task<IReadOnlyList<CodeInsightUnclassifiedFinding>> ListUnclassifiedAsync(
        int limit,
        int maxAttempts,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return this.WithDbAsync<IReadOnlyList<CodeInsightUnclassifiedFinding>>(
            async db =>
            {
                var records = await db.CodeInsightFindings
                    .Where(finding => finding.ClassifiedAt == null
                                      && finding.ClassificationAttempts < maxAttempts)
                    // Oldest first: a backlog should drain in the order it accumulated, so a burst does not
                    // starve the findings that were waiting before it.
                    .OrderBy(finding => finding.CreatedAt)
                    .Take(limit)
                    .Select(finding => new
                    {
                        finding.Id,
                        finding.CodeInsightPullRequestId,
                        finding.JobId,
                        finding.EncryptedMessage,
                        finding.FilePath,
                        finding.LineNumber,
                        finding.Severity,
                        finding.OriginPassKind,
                        finding.ClassificationAttempts,
                    })
                    .ToListAsync(ct);

                if (records.Count == 0)
                {
                    return [];
                }

                var aggregateIds = records.Select(record => record.CodeInsightPullRequestId).Distinct().ToList();
                var clientByAggregate = await db.CodeInsightPullRequests
                    .Where(pullRequest => aggregateIds.Contains(pullRequest.Id))
                    .Select(pullRequest => new { pullRequest.Id, pullRequest.ClientId })
                    .ToDictionaryAsync(pullRequest => pullRequest.Id, pullRequest => pullRequest.ClientId, ct);

                return records
                    .Where(record => clientByAggregate.ContainsKey(record.CodeInsightPullRequestId))
                    .Select(record => new CodeInsightUnclassifiedFinding(
                        record.Id,
                        clientByAggregate[record.CodeInsightPullRequestId],
                        record.JobId,
                        secretProtectionCodec.Unprotect(record.EncryptedMessage, FindingMessagePurpose),
                        record.FilePath,
                        record.LineNumber,
                        record.Severity,
                        record.OriginPassKind,
                        record.ClassificationAttempts))
                    .ToList();
            },
            ct);
    }

    public Task<int> CountUnclassifiedAsync(int maxAttempts, CancellationToken ct = default)
    {
        return this.WithDbAsync(
            db => db.CodeInsightFindings
                .CountAsync(
                    finding => finding.ClassifiedAt == null && finding.ClassificationAttempts < maxAttempts,
                    ct),
            ct);
    }

    public Task ApplyClassificationAsync(
        Guid findingId,
        CodeInsightClassification classification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(classification);

        return this.WithDbAsync(
            async db =>
            {
                var finding = await db.CodeInsightFindings
                    .FirstOrDefaultAsync(candidate => candidate.Id == findingId, ct);
                if (finding is null)
                {
                    return;
                }

                // Replace rather than add. A retry that partly succeeded, or a re-classification after a prompt
                // change, must not leave the finding carrying one type twice, every count it feeds would be
                // inflated and no constraint would catch it.
                var existingTags = await db.CodeInsightFindingTags
                    .Where(tag => tag.CodeInsightFindingId == findingId)
                    .ToListAsync(ct);
                db.CodeInsightFindingTags.RemoveRange(existingTags);

                var now = DateTimeOffset.UtcNow;

                foreach (var slug in classification.CoreSlugs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    db.CodeInsightFindingTags.Add(
                        new CodeInsightFindingTag
                        {
                            Id = Guid.CreateVersion7(),
                            CodeInsightFindingId = findingId,
                            IsCore = true,
                            CoreSlug = slug,
                            TaxonomyVersion = CodeInsightCoreTaxonomy.Version,
                            ClassifierVersion = classification.ClassifierVersion,
                            AssignedAt = now,
                        });
                }

                foreach (var customTagId in classification.CustomTagIds.Distinct())
                {
                    db.CodeInsightFindingTags.Add(
                        new CodeInsightFindingTag
                        {
                            Id = Guid.CreateVersion7(),
                            CodeInsightFindingId = findingId,
                            IsCore = false,
                            CustomTagId = customTagId,
                            TaxonomyVersion = CodeInsightCoreTaxonomy.Version,
                            ClassifierVersion = classification.ClassifierVersion,
                            AssignedAt = now,
                        });
                }

                finding.Level = classification.Level;
                finding.Qualifier = classification.Qualifier;
                finding.ClassificationConfidence = classification.Confidence;
                finding.ClassifiedAt = now;
                finding.ClassificationAttempts += 1;

                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task RecordClassificationAttemptAsync(Guid findingId, CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var finding = await db.CodeInsightFindings
                    .FirstOrDefaultAsync(candidate => candidate.Id == findingId, ct);
                if (finding is null)
                {
                    return;
                }

                finding.ClassificationAttempts += 1;
                await db.SaveChangesAsync(ct);
            },
            ct);
    }

    public Task<bool> RecordDispositionAsync(
        Guid findingId,
        CodeInsightDispositionRecord disposition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(disposition);

        return this.WithDbAsync(
            async db =>
            {
                var findingExists = await db.CodeInsightFindings
                    .AnyAsync(finding => finding.Id == findingId, ct);
                if (!findingExists)
                {
                    return false;
                }

                var existing = await db.CodeInsightFindingDispositions
                    .AnyAsync(candidate => candidate.CodeInsightFindingId == findingId, ct);
                if (existing)
                {
                    // Already decided. A crawl observes the same resolved thread on every pass, and a metric
                    // already computed from an outcome must not change underneath a report.
                    return false;
                }

                db.CodeInsightFindingDispositions.Add(
                    new CodeInsightFindingDisposition
                    {
                        Id = Guid.CreateVersion7(),
                        CodeInsightFindingId = findingId,
                        Disposition = disposition.Disposition,
                        SourceIntent = disposition.SourceIntent,
                        SourceCodeChange = disposition.SourceCodeChange,
                        ClassifierVersion = disposition.ClassifierVersion,
                        ClassifierConfidence = disposition.ClassifierConfidence,
                        RejectionReason = disposition.RejectionReason,
                        DecidedAt = DateTimeOffset.UtcNow,
                    });

                await db.SaveChangesAsync(ct);
                return true;
            },
            ct);
    }

    public Task<CodeInsightDispositionRecord?> GetDispositionAsync(
        Guid findingId,
        CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var record = await db.CodeInsightFindingDispositions
                    .FirstOrDefaultAsync(candidate => candidate.CodeInsightFindingId == findingId, ct);

                return record is null
                    ? null
                    : new CodeInsightDispositionRecord(
                        record.Disposition,
                        record.SourceIntent,
                        record.SourceCodeChange,
                        record.ClassifierVersion,
                        record.ClassifierConfidence,
                        record.RejectionReason);
            },
            ct);
    }

    public Task<bool> RecordMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(miss);

        return this.WithDbAsync(
            async db =>
            {
                // The aggregate is created if the pull request has no findings yet: a review that found
                // nothing while a human found something is exactly the case recall exists to capture.
                var pullRequest = await GetOrCreatePullRequestAsync(db, key, null, DateTimeOffset.UtcNow, ct);

                var alreadyHarvested = await db.CodeInsightMisses
                    .AnyAsync(
                        candidate => candidate.CodeInsightPullRequestId == pullRequest.Id
                                     && candidate.ProviderThreadId == miss.ProviderThreadId,
                        ct);
                if (alreadyHarvested)
                {
                    return false;
                }

                db.CodeInsightMisses.Add(
                    new CodeInsightMiss
                    {
                        Id = Guid.CreateVersion7(),
                        CodeInsightPullRequestId = pullRequest.Id,
                        ProviderThreadId = miss.ProviderThreadId,
                        FilePath = miss.FilePath,
                        LineNumber = miss.LineNumber,
                        EncryptedDiscussion = secretProtectionCodec.Protect(miss.Discussion, MissDiscussionPurpose),
                        IsSubstantive = miss.IsSubstantive,
                        WasActedOn = miss.WasActedOn,
                        IsInScope = miss.IsInScope,
                        CountsAsMiss = miss.CountsAsMiss,
                        ClassifierConfidence = miss.Confidence,
                        ClassifierVersion = miss.ClassifierVersion,
                        HarvestedAt = DateTimeOffset.UtcNow,
                    });

                await db.SaveChangesAsync(ct);
                return true;
            },
            ct);
    }

    public Task<bool> HasHarvestedThreadAsync(
        CodeInsightPullRequestKey key,
        string providerThreadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerThreadId);

        return this.WithDbAsync(
            async db =>
            {
                var aggregateId = await FindPullRequestIdAsync(db, key, ct);
                if (aggregateId is null)
                {
                    return false;
                }

                return await db.CodeInsightMisses
                    .AnyAsync(
                        miss => miss.CodeInsightPullRequestId == aggregateId.Value
                                && miss.ProviderThreadId == providerThreadId,
                        ct);
            },
            ct);
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
                        miss.CountsAsMiss,
                        miss.ClassifierConfidence,
                        miss.ClassifierVersion,
                        miss.HarvestedAt))
                    .ToList();
            },
            ct);
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var expired = await db.CodeInsightPullRequests
                    .Where(pullRequest => pullRequest.LastActivityAt < cutoff)
                    .ToListAsync(ct);

                return await RemovePullRequestsAsync(db, expired, ct);
            },
            ct);
    }

    public Task<int> PurgeForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return this.WithDbAsync(
            async db =>
            {
                var owned = await db.CodeInsightPullRequests
                    .Where(pullRequest => pullRequest.ClientId == clientId)
                    .ToListAsync(ct);

                return await RemovePullRequestsAsync(db, owned, ct);
            },
            ct);
    }

    // Purge each pull request in its own transaction: a single SaveChangesAsync removes everything collected
    // under that aggregate along with the aggregate row, so a crash mid-sweep leaves whole pull requests
    // either fully purged or fully intact.
    //
    // Every descendant is loaded and removed explicitly rather than left to the database cascade. Two reasons:
    // the in-memory provider used by the unit tests has no cascade at all, and (more importantly) a new
    // child table added later shows up here as a compile-time-adjacent omission rather than as orphaned rows
    // nobody notices. Add new child tables to this method.
    private static async Task<int> RemovePullRequestsAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<CodeInsightPullRequest> pullRequests,
        CancellationToken ct)
    {
        if (pullRequests.Count == 0)
        {
            return 0;
        }

        var removed = 0;

        foreach (var pullRequest in pullRequests)
        {
            var findings = await db.CodeInsightFindings
                .Where(finding => finding.CodeInsightPullRequestId == pullRequest.Id)
                .ToListAsync(ct);
            var findingIds = findings.Select(finding => finding.Id).ToList();

            var tags = await db.CodeInsightFindingTags
                .Where(tag => findingIds.Contains(tag.CodeInsightFindingId))
                .ToListAsync(ct);

            var dispositions = await db.CodeInsightFindingDispositions
                .Where(disposition => findingIds.Contains(disposition.CodeInsightFindingId))
                .ToListAsync(ct);

            var misses = await db.CodeInsightMisses
                .Where(miss => miss.CodeInsightPullRequestId == pullRequest.Id)
                .ToListAsync(ct);

            var metrics = await db.CodeInsightPullRequestMetrics
                .Where(metric => metric.CodeInsightPullRequestId == pullRequest.Id)
                .ToListAsync(ct);

            db.CodeInsightFindingTags.RemoveRange(tags);
            db.CodeInsightFindingDispositions.RemoveRange(dispositions);
            db.CodeInsightMisses.RemoveRange(misses);
            db.CodeInsightPullRequestMetrics.RemoveRange(metrics);
            db.CodeInsightFindings.RemoveRange(findings);
            db.CodeInsightPullRequests.Remove(pullRequest);

            await db.SaveChangesAsync(ct);
            removed++;
        }

        return removed;
    }

    private static async Task<Guid?> FindPullRequestIdAsync(
        MeisterProPRDbContext db,
        CodeInsightPullRequestKey key,
        CancellationToken ct)
    {
        var match = await db.CodeInsightPullRequests
            .Where(candidate => candidate.ClientId == key.ClientId
                                && candidate.RepositoryId == key.RepositoryId
                                && candidate.PullRequestId == key.PullRequestId)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);

        return match;
    }

    private static async Task<CodeInsightPullRequest> GetOrCreatePullRequestAsync(
        MeisterProPRDbContext db,
        CodeInsightPullRequestKey key,
        string? pullRequestState,
        DateTimeOffset? lastActivityAt,
        CancellationToken ct,
        string? repositoryName = null)
    {
        var pullRequest = await db.CodeInsightPullRequests
            .FirstOrDefaultAsync(
                candidate => candidate.ClientId == key.ClientId
                             && candidate.RepositoryId == key.RepositoryId
                             && candidate.PullRequestId == key.PullRequestId,
                ct);

        var now = DateTimeOffset.UtcNow;

        if (pullRequest is null)
        {
            pullRequest = new CodeInsightPullRequest
            {
                Id = Guid.CreateVersion7(),
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                PullRequestState = pullRequestState ?? string.Empty,
                RepositoryName = Trimmed(repositoryName),
                LastActivityAt = lastActivityAt ?? now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CodeInsightPullRequests.Add(pullRequest);
            return pullRequest;
        }

        if (pullRequestState is not null)
        {
            pullRequest.PullRequestState = pullRequestState;
        }

        // Only ever overwritten with a name somebody actually reported: a caller that does not know one (the
        // human-thread harvest, for instance) must not erase the name a review recorded.
        if (Trimmed(repositoryName) is { } name)
        {
            pullRequest.RepositoryName = name;
        }

        // The retention anchor only ever moves forward, so a late-arriving older observation cannot
        // shorten the window an aggregate has left.
        if (lastActivityAt is not null && lastActivityAt > pullRequest.LastActivityAt)
        {
            pullRequest.LastActivityAt = lastActivityAt.Value;
        }

        pullRequest.UpdatedAt = now;
        return pullRequest;
    }

    private static string? Trimmed(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private CodeInsightFindingView ToView(CodeInsightFinding record)
    {
        return new CodeInsightFindingView(
            record.Id,
            record.JobId,
            record.RevisionKey,
            record.Ordinal,
            record.FilePath,
            record.LineNumber,
            record.Severity,
            secretProtectionCodec.Unprotect(record.EncryptedMessage, FindingMessagePurpose),
            record.ProviderThreadId,
            record.ObservedAt);
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
}
