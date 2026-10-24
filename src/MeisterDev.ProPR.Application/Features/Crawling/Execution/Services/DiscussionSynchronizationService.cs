// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Processes changed discussion evidence under one durable, scoped target lease.</summary>
public sealed class DiscussionSynchronizationService(
    IDiscussionSynchronizationStore store,
    IPullRequestThreadSnapshotReader reader,
    IClientScmConnectionRepository connections,
    IScmProviderRegistry providers,
    ILogger<DiscussionSynchronizationService> logger,
    IOptions<DiscussionSynchronizationOptions> options,
    IPostedCommentOriginStore? origins = null,
    IReviewArchiveIngestionService? archive = null,
    ICodeInsightDiscussionBatchFactory? batches = null,
    ICodeInsightDispositionService? dispositions = null,
    ICodeInsightMissHarvester? misses = null,
    ICodeInsightHarvestCoverageRecorder? coverage = null,
    IThreadMemoryService? memory = null,
    IReviewPrScanThreadStatusStore? scans = null,
    IAiConfigurationVersionReader? aiVersions = null,
    TimeProvider? timeProvider = null,
    ICodeInsightModelConcernObserver? modelConcerns = null) : IDiscussionSynchronizationService
{
    private readonly Dictionary<PullRequestThreadSnapshotTarget, CachedObservation> _observations = [];

    public void Invalidate(PullRequestThreadSnapshotTarget target)
    {
        var source = target.GetEvidenceSourceId(providers);
        foreach (var cached in this._observations.Keys.Where(key => key.GetEvidenceSourceId(providers) == source).ToArray())
        {
            this._observations.Remove(cached);
        }
    }

    public async Task<PullRequestThreadSnapshotTarget?> ResolveTargetAsync(PullRequestSynchronizationRequest request, CancellationToken ct = default)
    {
        var connection = await this.ResolveConnectionAsync(request, ct);
        return connection is { IsActive: true } ? this.Target(request, connection.Id) : null;
    }

    public async Task RequestFromDeliveryAsync(PullRequestSynchronizationRequest request, string? threadId, Guid deliveryId, CancellationToken ct = default)
    {
        var target = await this.ResolveTargetAsync(request, ct);
        if (target is not null)
        {
            await store.RequestFromDeliveryAsync(target, threadId, deliveryId, ct);
        }
    }

    public async Task RequestAsync(PullRequestSynchronizationRequest request, string? threadId, CancellationToken ct = default)
    {
        var connection = await this.ResolveConnectionAsync(request, ct);
        if (connection is null)
        {
            return;
        }

        await store.RequestAsync(this.Target(request, connection.Id), threadId, ct);
    }

    public async Task<PullRequestThreadSnapshot> ObserveAsync(PullRequestSynchronizationRequest request, CancellationToken ct = default)
    {
        using var measurement = DiscussionSynchronizationMetrics.Begin(request.Provider);
        var connection = await this.ResolveConnectionAsync(request, ct) ??
                         throw new InvalidOperationException("The discussion provider connection is unavailable.");
        var target = this.Target(request, connection.Id);
        request = request with { RepositoryId = target.RepositoryId };
        var inputRevision = Revision(request);
        var interval = options.Value.ReconciliationInterval;
        var cached = await this.ReadCachedObservationAsync(request, connection, target, inputRevision, interval, ct);
        if (cached.Snapshot is not null)
        {
            measurement.Complete(0, 0, "completed");
            return cached.Snapshot;
        }

        await using var lease = await store.TryAcquireAsync(
            target, inputRevision, cached.RefreshRequired || request.PullRequestStatus != PrStatus.Active, interval, ct);
        if (lease is null)
        {
            measurement.Complete(0, 0, "not_due");
            return await this.ReadPreviousSummariesAsync(target, ct);
        }

        using var writeScope = lease.BeginWrites();
        await using var renewal = new DiscussionLeaseRenewal(lease, timeProvider ?? TimeProvider.System, ct);
        var operation = renewal.Token;
        PrStatus? authoritativeStatus = null;
        ReviewRevision? authoritativeRevision = null;
        var metadataObserved = RequiresAuthoritativeMetadata(request);
        try
        {
            await lease.EnsureCurrentAsync(operation);
            if (metadataObserved)
            {
                request = await this.ReadAuthoritativeMetadataAsync(request, connection.Id, operation);
                authoritativeStatus = request.PullRequestStatus;
                authoritativeRevision = request.ReviewRevision;
            }

            var revision = Revision(request) ?? inputRevision;
            var prepared = await this.PrepareObservationAsync(request, connection, lease.State, revision, operation);
            var snapshot = await this.ReadSnapshotAsync(request, connection, target, lease, prepared, revision, interval, operation);
            var context = new ObservationContext(request, connection, target, lease, prepared, snapshot, revision, inputRevision, metadataObserved, interval);
            var result = await this.ProcessObservationAsync(context, renewal, operation);
            measurement.Complete(
                context.Changed, context.Unchanged,
                context.AllCurrent ? (context.UnavailableNativeEvidence ? "coverage_unavailable" : "completed") : "pending_consumers");
            logger.LogDebug(
                "Discussion synchronization completed for PR {PullRequestId}: {ChangedThreads} changed, {UnchangedThreads} unchanged, {SkippedFindings} unmatched findings.",
                request.PullRequestId, context.Changed, context.Unchanged, context.SkippedFindings);
            return result;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            await renewal.StopAsync();
            if (authoritativeStatus.HasValue)
            {
                throw new DiscussionObservationUnavailableException(authoritativeStatus.Value, authoritativeRevision, renewal.Failure ?? exception);
            }

            renewal.ThrowIfLost();
            throw;
        }
    }

    private async Task<(PullRequestThreadSnapshot? Snapshot, bool RefreshRequired)> ReadCachedObservationAsync(
        PullRequestSynchronizationRequest request, ClientScmConnectionDto connection, PullRequestThreadSnapshotTarget target,
        string? revision, TimeSpan interval, CancellationToken ct)
    {
        if (!this._observations.TryGetValue(target, out var cached))
        {
            return (null, false);
        }

        var current = await store.ReadAsync(target, ct);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (current is not null && current.LeaseToken is null && current.RequestedGeneration == cached.RequestedGeneration &&
            current.CompletedGeneration >= current.RequestedGeneration && current.CollectionGeneration == cached.CollectionGeneration &&
            current.RetentionGeneration == cached.RetentionGeneration && !current.FullRefreshRequested &&
            current.LastObservedAt == cached.Snapshot.ObservedAt && current.LastReviewRevision == cached.Revision &&
            current.LastFullReconciledAt > now - interval &&
            (request.PullRequestStatus == cached.RequestedStatus || request.PullRequestStatus == cached.Snapshot.ObservedPullRequestStatus ||
             request.PullRequestStatus is PrStatus.Completed or PrStatus.Abandoned &&
             cached.Snapshot.ObservedPullRequestStatus is PrStatus.Completed or PrStatus.Abandoned) &&
            (revision is null || revision == cached.InputRevision || revision == cached.Revision) &&
            (!RequiresAuthoritativeMetadata(request) || cached.MetadataObserved) &&
            await store.IsCurrentObservationAsync(target, cached.Snapshot.ObservedAt, ct))
        {
            var prepared = await this.PrepareObservationAsync(request, connection, current, cached.Revision, ct);
            if (prepared.Versions == cached.Versions)
            {
                return (cached.Snapshot, false);
            }
        }

        this.Invalidate(target);
        reader.Invalidate(target);
        return (null, true);
    }

    private async Task<PullRequestThreadSnapshot> ReadPreviousSummariesAsync(PullRequestThreadSnapshotTarget target, CancellationToken ct)
    {
        var current = await store.ReadAsync(target, ct);
        return new PullRequestThreadSnapshot(
            current?.LastObservedAt ?? (timeProvider ?? TimeProvider.System).GetUtcNow(), [],
            JsonSerializer.Deserialize<PrThreadStatusEntry[]>(current?.ReviewerSummariesJson ?? "[]") ?? [], false)
        {
            ReadFailure = current?.LastObservedAt is null
                ? new InvalidOperationException("Discussion evidence has not been observed under a target lease.")
                : null,
        };
    }

    private static string? Revision(PullRequestSynchronizationRequest request) =>
        ReviewRevisionKeys.TryGetStoredKey(request.ReviewRevision) ?? request.CandidateIterationId?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool RequiresAuthoritativeMetadata(PullRequestSynchronizationRequest request) =>
        request.DiscussionOnly || request.PullRequestStatus != PrStatus.Active || request.ActivationSource == PullRequestActivationSource.Webhook;

    private async Task<PullRequestSynchronizationRequest> ReadAuthoritativeMetadataAsync(
        PullRequestSynchronizationRequest request, Guid connectionId, CancellationToken ct)
    {
        var repository = request.Repository ?? providers.GetReviewSourcePolicy(request.Provider).CreateCapturedRepository(
            request.ProviderScopePath, request.RepositoryId, request.ProviderProjectKey, request.RepositoryId);
        var review = request.CodeReview ?? new CodeReviewRef(
            repository, CodeReviewPlatformKind.PullRequest,
            request.PullRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), request.PullRequestId);
        var observed = await providers.GetCodeReviewQueryService(request.Provider).GetReviewAsync(
                           request.ClientId, review, ct, new(connectionId, request.ProviderScopePath)) ??
                       throw new InvalidOperationException("The discussion pull request could not be read.");
        var status = observed.ReviewState switch
        {
            CodeReviewState.Closed => PrStatus.Abandoned,
            CodeReviewState.Merged => PrStatus.Completed,
            CodeReviewState.Open or CodeReviewState.Draft => PrStatus.Active,
            _ => throw new InvalidOperationException("The discussion pull-request state is unavailable."),
        };
        return request with { ReviewRevision = observed.ReviewRevision, PullRequestStatus = status };
    }

    private async Task<PreparedObservation> PrepareObservationAsync(
        PullRequestSynchronizationRequest request, ClientScmConnectionDto connection, DiscussionSynchronizationState state,
        string? revision, CancellationToken ct)
    {
        var scope = ProviderSourceIdentity.FromReviewSource(
            request.Provider,
            providers.GetSourceIdentityPolicy(request.Provider).SelectCapturedSource(request.ProviderScopePath, connection.HostBaseUrl),
            providers.GetSourceIdentityPolicy(request.Provider)).Value;
        var sourceOrigins = origins is null
            ? null
            : await origins.GetOriginsForProviderSourceAsync(request.ClientId, request.RepositoryId, request.PullRequestId, request.Provider, scope, ct);
        var provenance = sourceOrigins?.Matched;
        var progress = state.ReadProgress();
        var key = new CodeInsightPullRequestKey(request.ClientId, request.RepositoryId, request.PullRequestId);
        var batch = batches is null ? null : await batches.CreateAsync(key, scope, ct);
        if (batch is not null)
        {
            batch = batch with { ConnectionId = connection.Id };
        }

        var commentScope = providers.GetIdentityPolicy(request.Provider).CommentIdScope;
        var scopedFindings = batch?.Findings.Where(finding => finding.ProviderScope == scope).ToArray() ?? [];
        var receipts = scopedFindings.Where(finding => !string.IsNullOrWhiteSpace(finding.ProviderCommentId) &&
                                                       (commentScope == ProviderCommentIdScope.PullRequest ||
                                                        !string.IsNullOrWhiteSpace(finding.ProviderThreadId)))
            .Select(finding => new PostedCommentOriginRow(finding.ProviderThreadId, finding.ProviderCommentId!, finding.JobId));
        var ownership = ThreadOwnershipResolver.Create(
            (provenance ?? []).Concat(receipts).ToArray(), ThreadOwnerIdentity.None,
            commentScope, scopedFindings.Where(finding => !string.IsNullOrWhiteSpace(finding.ProviderThreadId))
                .Select(finding => finding.ProviderThreadId!).ToHashSet(StringComparer.Ordinal));
        var scan = scans is null
            ? null
            : await scans.GetAsync(
                request.ClientId, request.ProviderScopePath, request.ProviderProjectKey,
                request.RepositoryId, request.PullRequestId, ct);
        if (scan is null && scans is not null && memory is not null)
        {
            scan = await scans.FindByProviderCoordinatesAsync(
                request.ClientId, request.Provider, request.ProviderScopePath,
                request.ProviderProjectKey, request.RepositoryId, request.PullRequestId, ct);
        }

        var dispositionVersion = batch is null
            ? null
            : DiscussionThreadProgress.Fingerprint(
                batch.DependencyVersion, state.CollectionGeneration.ToString(), dispositions?.DependencyVersion ?? "", revision ?? "");
        var missVersion = batch is null
            ? null
            : DiscussionThreadProgress.Fingerprint(batch.DependencyVersion, state.CollectionGeneration.ToString(), misses?.DependencyVersion ?? "");
        string? modelVersion = null;
        var modelPreparationCurrent = true;
        if (batch is not null && modelConcerns is not null)
        {
            try
            {
                modelVersion = DiscussionThreadProgress.Fingerprint(await modelConcerns.PrepareAsync(batch, ct), state.CollectionGeneration.ToString());
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                modelPreparationCurrent = false;
                modelVersion = "model-evidence-preparation-unavailable";
                logger.LogWarning("Independent model concern preparation unavailable: {FailureType}", exception.GetType().Name);
            }
        }

        var archiveVersion = DiscussionThreadProgress.Fingerprint(
            "archive-v2", state.RetentionGeneration.ToString(), connection.StoreThreads.ToString(), connection.RetentionDays?.ToString() ?? "");
        var memoryConfiguration = memory is not null && scan is not null && aiVersions is not null
            ? await aiVersions.ReadAsync(request.ClientId, [AiPurpose.EmbeddingDefault, AiPurpose.MemoryReconsideration], ct)
            : "";
        var memoryVersion = DiscussionThreadProgress.Fingerprint("memory-v2", scan?.Id.ToString() ?? "", memoryConfiguration);
        var unverifiable = ThreadOwnershipResolver.Create(sourceOrigins?.Unverifiable ?? [], ThreadOwnerIdentity.None, commentScope);
        var priorReviewerIds = (JsonSerializer.Deserialize<PrThreadStatusEntry[]>(state.ReviewerSummariesJson) ?? [])
            .Where(thread => thread.ThreadId is not null).Select(thread => thread.ThreadId!).ToHashSet(StringComparer.Ordinal);
        return new(
            scope, key, batch, scopedFindings, progress, ownership, unverifiable, scan,
            new(dispositionVersion, missVersion, archiveVersion, memoryVersion, modelVersion), priorReviewerIds, modelPreparationCurrent);
    }

    private async Task<PullRequestThreadSnapshot> ReadSnapshotAsync(
        PullRequestSynchronizationRequest request, ClientScmConnectionDto connection, PullRequestThreadSnapshotTarget target,
        IDiscussionSynchronizationLease lease,
        PreparedObservation prepared, string? revision, TimeSpan interval, CancellationToken ct)
    {
        var pendingThreadIds = JsonSerializer.Deserialize<string[]>(lease.State.PendingThreadIdsJson) ?? [];
        var fullDue = request.PullRequestStatus != PrStatus.Active || lease.State.FullRefreshRequested ||
                      lease.State.LastFullReconciledAt is null || lease.State.LastFullReconciledAt <= lease.ObservedAt - interval ||
                      revision != lease.State.LastReviewRevision;
        var catchUp = prepared.Progress.Count == 0 || prepared.Progress.Any(entry =>
        {
            var state = entry.Value;
            return
                archive is not null && connection.StoreThreads && state.Needs("archive", state.SourceFingerprint, prepared.Versions.Archive) ||
                prepared.Batch is not null && dispositions is not null && state.Needs("disposition", state.SourceFingerprint, prepared.Versions.Disposition!) ||
                prepared.Batch is not null && misses is not null && state.Needs("miss", state.SourceFingerprint, prepared.Versions.Miss!) ||
                prepared.Versions.ModelEvidence is not null && state.Needs("modelEvidence", state.SourceFingerprint, prepared.Versions.ModelEvidence) ||
                memory is not null && prepared.Scan is not null && (prepared.PriorReviewerIds.Contains(entry.Key) || state.MemoryPending) &&
                state.Needs("memory", state.SourceFingerprint, prepared.Versions.Memory);
        });
        var targeted = !fullDue && !catchUp && pendingThreadIds.Length > 0;
        var rawSnapshot = targeted
            ? await reader.ReadTargetedAsync(target, pendingThreadIds, prepared.Ownership, ct)
            : await reader.ReadAsync(target, prepared.Ownership, ct);
        var snapshot = rawSnapshot with
        {
            ObservedAt = lease.ObservedAt, ObservedReviewRevision = request.ReviewRevision, ObservedPullRequestStatus = request.PullRequestStatus
        };
        if (!snapshot.EnumerationComplete && (!targeted || snapshot.TargetThreadIds is null ||
                                              !snapshot.TargetThreadIds.ToHashSet(StringComparer.Ordinal).SetEquals(pendingThreadIds) ||
                                              !snapshot.Threads.Select(thread => thread.ThreadId).ToHashSet(StringComparer.Ordinal)
                                                  .SetEquals(pendingThreadIds)))
        {
            throw new InvalidOperationException("The provider did not complete the requested discussion observation.");
        }

        if (snapshot.Threads.Any(thread => thread.Comments.Any(comment =>
            {
                var reference = new ThreadCommentRef(
                    thread.ThreadId, comment.CommentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    comment.AuthorId, comment.AuthorNativeId ?? comment.AuthorName);
                return prepared.Unverifiable.OwnsComment(reference) && !prepared.Ownership.OwnsComment(reference);
            })))
        {
            throw new InvalidOperationException("Provider discussion ownership could not be verified in the captured source.");
        }

        return snapshot;
    }

    private async Task<PullRequestThreadSnapshot> ProcessObservationAsync(ObservationContext context, DiscussionLeaseRenewal renewal, CancellationToken ct)
    {
        try
        {
            await context.Lease.EnsureCurrentAsync(ct);
            var present = context.Snapshot.Threads.Select(thread => thread.ThreadId).ToHashSet(StringComparer.Ordinal);
            var known = context.Prepared.Progress.Keys.Concat(
                context.Prepared.Findings.Select(finding => finding.ProviderThreadId)
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)).ToHashSet(StringComparer.Ordinal);
            var threads = context.Snapshot.EnumerationComplete
                ? context.Snapshot.Threads.Concat(
                    known.Where(id => !present.Contains(id))
                        .Select(id => new PrCommentThread(id, null, null, [], "Deleted", true)))
                : context.Snapshot.Threads;
            foreach (var thread in threads)
            {
                await this.ProcessThreadAsync(context, thread, ct);
            }

            await this.PersistArchiveAsync(context, ct);
            await this.PersistMemoryStatusesAsync(context, ct);
            await this.RecordCoverageAsync(context, ct);
            if (modelConcerns is not null && context.Prepared.Batch is not null && context.Snapshot.EnumerationComplete)
            {
                await context.Lease.EnsureCurrentAsync(ct);
                try
                {
                    var retained = await modelConcerns.CompleteEnumerationAsync(
                        context.Prepared.Key, context.Prepared.Scope, context.Connection.Id,
                        context.Snapshot.ObservedAt, context.ModelConcernsRetained && context.Prepared.ModelPreparationCurrent,
                        context.ModelIdentities.ToArray(), ct);
                    context.ModelEvidenceCurrent &= retained;
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    context.ModelEvidenceCurrent = false;
                    logger.LogWarning("Independent model concern finalization unavailable: {FailureType}", exception.GetType().Name);
                }
            }

            return await this.CompleteObservationAsync(context, renewal, ct);
        }
        catch
        {
            reader.Invalidate(context.Target);
            await renewal.StopAsync();
            if (renewal.Failure is null)
            {
                await this.CheckpointPartialProgressAsync(context);
            }

            throw;
        }
    }

    private async Task ProcessThreadAsync(ObservationContext context, PrCommentThread thread, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(thread.ThreadId))
        {
            var unaddressable = ThreadUpdatedEventFactory.Build(
                context.Request.ClientId, context.Connection.Id, context.Request.RepositoryId,
                context.Request.PullRequestId, thread, context.Prepared.Ownership, context.Snapshot.ObservedAt, context.Prepared.Scope);
            await this.ObserveModelEvidenceAsync(context, unaddressable, null, null, ct);
            if (ThreadUpdatedEventFactory.IsHumanThread(unaddressable))
            {
                context.HarvestCoverage.Observe(
                    unaddressable, providers.GetCodeReviewPreparationPolicy(context.Request.Provider).InterpretThreadResolution(thread.Status));
                context.HarvestCoverage.RecordRetention(false);
                context.UnavailableNativeEvidence = true;
                context.InsightsCurrent = false;
            }

            return;
        }

        if (!context.Prepared.Progress.TryGetValue(thread.ThreadId, out var state))
        {
            context.Prepared.Progress[thread.ThreadId] = state = new DiscussionThreadProgress();
        }

        context.Statuses.TryGetValue(thread.ThreadId, out var status);
        if (!context.Connection.StoreThreads)
        {
            state.Consumers.Remove("archive");
        }

        if (context.Prepared.Batch is null)
        {
            state.Consumers.Remove("miss");
            state.Consumers.Remove("disposition");
        }

        if (context.Prepared.Versions.ModelEvidence is null)
        {
            state.Consumers.Remove("modelEvidence");
        }

        var visible = thread with { Comments = thread.Comments.Where(comment => !comment.IsDeleted).ToArray() };
        var observation = ThreadUpdatedEventFactory.Build(
            context.Request.ClientId, context.Connection.Id, context.Request.RepositoryId, context.Request.PullRequestId,
            visible, context.Prepared.Ownership, context.Snapshot.ObservedAt, context.Prepared.Scope);
        var digest = DiscussionThreadProgress.Fingerprint(
            DiscussionFingerprints.CanonicalThread(thread), JsonSerializer.Serialize(status),
            JsonSerializer.Serialize(
                observation.Comments.OrderBy(comment => comment.CommentId, StringComparer.Ordinal)
                    .Select(comment => new { comment.CommentId, comment.IsAiAuthored, comment.OriginatingJobId })));
        if (state.SourceFingerprint == digest)
        {
            context.Unchanged++;
        }
        else
        {
            context.Changed++;
        }

        if (!thread.IsDeleted)
        {
            context.HarvestCoverage.Observe(
                observation, providers.GetCodeReviewPreparationPolicy(context.Request.Provider).InterpretThreadResolution(observation.Status));
        }

        var observationState = new ObservedThread(thread, visible, observation, state, status, digest);
        this.CollectArchiveChanges(context, observationState);
        await this.ObserveModelEvidenceAsync(context, observation, state, digest, ct);
        await this.ObserveDispositionAsync(context, observationState, ct);
        await this.ObserveMissAsync(context, observationState, ct);
        await this.ObserveMemoryAsync(context, observationState, ct);
        state.SourceFingerprint = digest;
    }

    private async Task ObserveModelEvidenceAsync(
        ObservationContext context, ThreadUpdatedEvent observation,
        DiscussionThreadProgress? state, string? digest, CancellationToken ct)
    {
        if (modelConcerns is null || !context.Prepared.ModelPreparationCurrent || context.Prepared.Versions.ModelEvidence is not { } version)
        {
            return;
        }

        if (!observation.IsDeleted && ModelConcernIdentity.Of(observation) is { } identity)
        {
            context.ModelIdentities.Add(identity);
        }

        if (state is not null && !state.Needs("modelEvidence", digest!, version))
        {
            return;
        }

        await context.Lease.EnsureCurrentAsync(ct);
        try
        {
            var ack = await modelConcerns.ObserveAsync(observation, ct);
            context.ModelConcernsRetained &= ack.ConcernRetained;
            context.ModelEvidenceCurrent &= ack.ConcernRetained && ack.ModelsCurrent;
            if (ack.ConcernRetained && ack.ModelsCurrent)
            {
                state?.Acknowledge("modelEvidence", digest!, version);
            }
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            context.ModelConcernsRetained = false;
            context.ModelEvidenceCurrent = false;
            logger.LogWarning("Independent model concern observation unavailable: {FailureType}", exception.GetType().Name);
        }
    }

    private void CollectArchiveChanges(ObservationContext context, ObservedThread observed)
    {
        var thread = observed.Thread;
        var state = observed.Progress;
        var observation = observed.Event;
        var digest = observed.Fingerprint;
        if (archive is not null && context.Connection.StoreThreads && state.Needs("archive", digest, context.Prepared.Versions.Archive))
        {
            context.ChangedArchive.Add(
                new RetainedThreadSnapshot(
                    observed.Event.ThreadId, thread.FilePath, thread.LineNumber, thread.Status ?? "", thread.LastUpdatedAt ?? thread.Comments
                        .Select(comment => comment.LastUpdatedAt ?? comment.PublishedAt)
                        .Where(stamp => stamp.HasValue).Max() ?? context.Snapshot.ObservedAt,
                    observation.Comments.Select(comment => new RetainedCommentSnapshot(
                        comment.CommentId, comment.AuthorIdentity, comment.IsAiAuthored,
                        comment.PublishedAt, comment.Text, comment.OriginatingJobId)).ToArray(), thread.IsDeleted, thread.ConcernAnchor));
            context.ArchiveAcknowledgments.Add((state, digest));
        }
    }

    private async Task ObserveDispositionAsync(ObservationContext context, ObservedThread observed, CancellationToken ct)
    {
        var thread = observed.Thread;
        var state = observed.Progress;
        var status = observed.ReviewerStatus;
        var digest = observed.Fingerprint;
        if (context.Prepared.Batch is not null && dispositions is not null && context.Prepared.Versions.Disposition is not null &&
            state.Needs("disposition", digest, context.Prepared.Versions.Disposition))
        {
            var finding = context.Prepared.Findings.FirstOrDefault(finding => finding.ProviderThreadId == observed.Event.ThreadId);
            if (finding is null)
            {
                state.Acknowledge("disposition", digest, context.Prepared.Versions.Disposition);
                context.SkippedFindings++;
            }
            else if (status is null || thread.IsDeleted)
            {
                if (thread.IsDeleted)
                {
                    context.UnavailableNativeEvidence = true;
                }
                else
                {
                    context.AllCurrent = false;
                }

                context.InsightsCurrent = false;
            }
            else
            {
                await context.Lease.EnsureCurrentAsync(ct);
                if (!context.DispositionPrepared)
                {
                    await dispositions.PrepareDiscussionBatchAsync(context.Prepared.Batch, ct);
                    context.DispositionPrepared = true;
                }

                var acknowledgement = await dispositions.ObserveCurrentAsync(
                    new ThreadResolvedDomainEvent(
                        context.Request.ClientId, context.Request.ProviderScopePath,
                        context.Request.ProviderProjectKey, context.Request.RepositoryId, context.Request.PullRequestId, observed.Event.ThreadId,
                        thread.FilePath, null,
                        status.CommentHistory,
                        context.Snapshot.ObservedAt,
                        providers.GetCodeReviewPreparationPolicy(context.Request.Provider).InterpretThreadResolution(status.Status),
                        status.CodeChangedSinceRaised, status.Status, context.Prepared.Scope), ct);
                if (acknowledgement.Retained)
                {
                    state.Acknowledge("disposition", digest, context.Prepared.Versions.Disposition);
                }
                else
                {
                    context.AllCurrent = false;
                    context.InsightsCurrent = false;
                }
            }
        }
    }

    private async Task ObserveMissAsync(ObservationContext context, ObservedThread observed, CancellationToken ct)
    {
        var thread = observed.Thread;
        var state = observed.Progress;
        var observation = observed.Event;
        var digest = observed.Fingerprint;
        if (context.Prepared.Batch is not null && misses is not null && context.Prepared.Versions.Miss is not null &&
            state.Needs("miss", digest, context.Prepared.Versions.Miss))
        {
            await context.Lease.EnsureCurrentAsync(ct);
            if (!context.MissPrepared)
            {
                await misses.PrepareDiscussionBatchAsync(context.Prepared.Batch, ct);
                context.MissPrepared = true;
            }

            var acknowledgement = await misses.ObserveCurrentAsync(observation with { IsDeleted = thread.IsDeleted }, ct);
            if (acknowledgement.Retained)
            {
                state.Acknowledge("miss", digest, context.Prepared.Versions.Miss);
            }
            else
            {
                context.AllCurrent = false;
                context.InsightsCurrent = false;
            }

            context.HarvestCoverage.RecordRetention(acknowledgement.Retained);
        }
    }


    private async Task ObserveMemoryAsync(ObservationContext context, ObservedThread observed, CancellationToken ct)
    {
        var thread = observed.Thread;
        var visible = observed.Visible;
        var state = observed.Progress;
        var status = observed.ReviewerStatus;
        var digest = observed.Fingerprint;
        if (memory is not null && scans is not null && context.Prepared.Scan is not null &&
            (status is not null || context.Prepared.PriorReviewerIds.Contains(observed.Event.ThreadId) || state.Consumers.ContainsKey("memory") ||
             state.MemoryPending) &&
            state.Needs("memory", digest, context.Prepared.Versions.Memory))
        {
            state.MemoryPending = true;
            if (context.Request.DeferMemoryProcessing && context.Request.PullRequestStatus == PrStatus.Active)
            {
                context.AllCurrent = false;
                return;
            }

            await context.Lease.EnsureCurrentAsync(ct);
            state.Consumers.Remove("memory");
            state.MemoryCheckpoint = null;
            await context.Lease.CheckpointProgressAsync(JsonSerializer.Serialize(context.Prepared.Progress), ct);
            var retainedMemory = true;
            try
            {
                var before = context.Prepared.Scan.Threads.FirstOrDefault(stored => stored.ThreadId == observed.Event.ThreadId)?.LastSeenStatus;
                var intent = providers.GetCodeReviewPreparationPolicy(context.Request.Provider).InterpretThreadResolution(status?.Status);
                var resolved = ThreadResolutionStatusInterpreter.IsResolved(intent);
                var wasResolved = ThreadResolutionStatusInterpreter.IsResolved(providers.CompatibilityCodec.DecodeStoredThreadResolution(before));
                if (resolved && status is not null && visible.Comments.Count > 0)
                {
                    retainedMemory = await memory.ObserveResolvedAsync(
                        new ThreadResolvedDomainEvent(
                            context.Request.ClientId, context.Prepared.Scan.OrganizationUrl, context.Prepared.Scan.ProjectId,
                            context.Prepared.Scan.RepositoryId, context.Request.PullRequestId, observed.Event.ThreadId, thread.FilePath, null,
                            status.CommentHistory,
                            context.Snapshot.ObservedAt, intent, status.CodeChangedSinceRaised, status.Status, context.Prepared.Scope), ct);
                }
                else if (wasResolved || thread.IsDeleted || status is null || visible.Comments.Count == 0)
                {
                    retainedMemory = await memory.ObserveReopenedAsync(
                        new ThreadReopenedDomainEvent(
                            context.Request.ClientId, context.Prepared.Scan.OrganizationUrl,
                            context.Prepared.Scan.ProjectId, context.Prepared.Scan.RepositoryId, context.Request.PullRequestId, observed.Event.ThreadId,
                            context.Snapshot.ObservedAt), ct);
                }
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                context.AllCurrent = false;
                logger.LogWarning(
                    "Discussion memory observation failed for PR {PullRequestId}: {ExceptionType}, code {ErrorCode}.", context.Request.PullRequestId,
                    exception.GetType().Name, exception.HResult);
                return;
            }

            if (!retainedMemory)
            {
                context.AllCurrent = false;
                return;
            }

            context.StatusUpdates[observed.Event.ThreadId] = status?.Status;
            context.MemoryAcknowledgments.Add((state, digest));
            await this.PersistMemoryStatusesAsync(context, ct);
        }
    }

    private async Task PersistArchiveAsync(ObservationContext context, CancellationToken ct)
    {
        if (archive is not null && context.Connection.StoreThreads)
        {
            await context.Lease.EnsureCurrentAsync(ct);
            try
            {
                var currentConnection = await connections.GetByIdAsync(context.Request.ClientId, context.Connection.Id, ct);
                if (currentConnection is not { IsActive: true, StoreThreads: true })
                {
                    context.AllCurrent = false;
                    throw new InvalidOperationException("Thread retention was disabled before persistence.");
                }

                await archive.HandleThreadsUpdatedAsync(
                    new(context.Request.ClientId, context.Connection.Id, context.Request.RepositoryId, context.Request.PullRequestId),
                    context.Request.PullRequestStatus.ToString(), context.Snapshot.ObservedAt, context.ChangedArchive, ct);
                foreach (var (state, digest) in context.ArchiveAcknowledgments)
                {
                    state.Acknowledge("archive", digest, context.Prepared.Versions.Archive);
                }
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                context.AllCurrent = false;
                logger.LogWarning(
                    "Discussion archive observation failed for PR {PullRequestId}: {ExceptionType}, code {ErrorCode}.", context.Request.PullRequestId,
                    exception.GetType().Name, exception.HResult);
            }
        }
    }

    private async Task PersistMemoryStatusesAsync(ObservationContext context, CancellationToken ct)
    {
        if (context.StatusUpdates.Count > 0 && scans is not null)
        {
            await context.Lease.EnsureCurrentAsync(ct);
            await scans.SetLastSeenStatusesAsync(
                context.Request.ClientId, context.Prepared.Scan!.OrganizationUrl, context.Prepared.Scan.ProjectId,
                context.Prepared.Scan.RepositoryId, context.Request.PullRequestId, context.StatusUpdates, ct);
            foreach (var (state, digest) in context.MemoryAcknowledgments)
            {
                state.Acknowledge("memory", digest, context.Prepared.Versions.Memory);
                state.MemoryPending = false;
            }

            await context.Lease.CheckpointProgressAsync(JsonSerializer.Serialize(context.Prepared.Progress), ct);
            context.StatusUpdates.Clear();
            context.MemoryAcknowledgments.Clear();
        }
    }

    private async Task RecordCoverageAsync(ObservationContext context, CancellationToken ct)
    {
        if (context.Snapshot.EnumerationComplete && context.Prepared.Batch is not null && coverage is not null)
        {
            await context.Lease.EnsureCurrentAsync(ct);
            var retainedCoverage = await coverage.RecordCurrentAcknowledgedAsync(
                context.Prepared.Key, context.Prepared.Scope, context.HarvestCoverage.AllHumanThreadsResolved, context.Snapshot.ObservedAt, ct,
                context.InsightsCurrent && context.HarvestCoverage.AllHumanObservationsRetained);
            if (!retainedCoverage.Retained)
            {
                if (!context.UnavailableNativeEvidence)
                {
                    context.AllCurrent = false;
                }

                context.InsightsCurrent = false;
            }
        }
    }

    private async Task<PullRequestThreadSnapshot> CompleteObservationAsync(ObservationContext context, DiscussionLeaseRenewal renewal, CancellationToken ct)
    {
        await renewal.StopAsync();
        renewal.ThrowIfLost();
        await context.Lease.EnsureCurrentAsync(ct);
        var snapshot = context.Snapshot;
        var summaries = MergeReviewerSummaries(snapshot, context.Lease.State.ReviewerSummariesJson);
        await context.Lease.CompleteAsync(
            JsonSerializer.Serialize(context.Prepared.Progress), JsonSerializer.Serialize(summaries), snapshot.ObservedAt,
            snapshot.EnumerationComplete, context.AllCurrent && context.ModelEvidenceCurrent,
            context.Request.PullRequestStatus != PrStatus.Active && !context.UnavailableNativeEvidence, context.Revision, context.Interval, ct);
        if (context.Lease.CompletionIsCurrent && context.AllCurrent && snapshot.EnumerationComplete && context.InsightsCurrent &&
            coverage is not null && misses is not null && dispositions is not null)
        {
            snapshot = snapshot with { CurrentConsumersRetained = true };
            reader.AcknowledgeCurrentConsumers(context.Target, snapshot);
        }

        if (context.Lease.CompletionIsCurrent && context.AllCurrent && context.ModelEvidenceCurrent && !context.UnavailableNativeEvidence &&
            snapshot.EnumerationComplete)
        {
            var state = context.Lease.State;
            this._observations[context.Target] = new(
                snapshot, state.RequestedGeneration, state.CollectionGeneration, state.RetentionGeneration,
                context.Request.PullRequestStatus, context.Revision, context.InputRevision, context.MetadataObserved, context.Prepared.Versions);
        }
        else
        {
            reader.Invalidate(context.Target);
        }

        return snapshot.EnumerationComplete ? snapshot : snapshot with { ReviewerThreads = summaries };
    }

    private async Task CheckpointPartialProgressAsync(ObservationContext context)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await this.PersistMemoryStatusesAsync(context, cleanup.Token);
            await context.Lease.CheckpointProgressAsync(JsonSerializer.Serialize(context.Prepared.Progress), cleanup.Token);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                "Partial discussion progress could not be persisted for PR {PullRequestId}: {ExceptionType}, code {ErrorCode}.",
                context.Request.PullRequestId, exception.GetType().Name, exception.HResult);
        }
    }

    private sealed record ConsumerVersions(string? Disposition, string? Miss, string Archive, string Memory, string? ModelEvidence);

    private sealed record CachedObservation(
        PullRequestThreadSnapshot Snapshot,
        long RequestedGeneration,
        long CollectionGeneration,
        long RetentionGeneration,
        PrStatus RequestedStatus,
        string? Revision,
        string? InputRevision,
        bool MetadataObserved,
        ConsumerVersions Versions);

    private sealed record PreparedObservation(
        string Scope,
        CodeInsightPullRequestKey Key,
        CodeInsightDiscussionBatch? Batch,
        IReadOnlyList<CodeInsightFindingView> Findings,
        Dictionary<string, DiscussionThreadProgress> Progress,
        ThreadOwnershipResolver Ownership,
        ThreadOwnershipResolver Unverifiable,
        ReviewPrScan? Scan,
        ConsumerVersions Versions,
        HashSet<string> PriorReviewerIds,
        bool ModelPreparationCurrent);

    private sealed record ObservedThread(
        PrCommentThread Thread,
        PrCommentThread Visible,
        ThreadUpdatedEvent Event,
        DiscussionThreadProgress Progress,
        PrThreadStatusEntry? ReviewerStatus,
        string Fingerprint);

    private sealed class ObservationContext(
        PullRequestSynchronizationRequest request,
        ClientScmConnectionDto connection,
        PullRequestThreadSnapshotTarget target,
        IDiscussionSynchronizationLease lease,
        PreparedObservation prepared,
        PullRequestThreadSnapshot snapshot,
        string? revision,
        string? inputRevision,
        bool metadataObserved,
        TimeSpan interval)
    {
        public PullRequestSynchronizationRequest Request { get; } = request;
        public ClientScmConnectionDto Connection { get; } = connection;
        public PullRequestThreadSnapshotTarget Target { get; } = target;
        public IDiscussionSynchronizationLease Lease { get; } = lease;
        public PreparedObservation Prepared { get; } = prepared;
        public PullRequestThreadSnapshot Snapshot { get; } = snapshot;
        public string? Revision { get; } = revision;
        public string? InputRevision { get; } = inputRevision;
        public bool MetadataObserved { get; } = metadataObserved;
        public TimeSpan Interval { get; } = interval;

        public Dictionary<string, PrThreadStatusEntry> Statuses { get; } = snapshot.ReviewerThreads.Where(thread => thread.ThreadId is not null)
            .ToDictionary(thread => thread.ThreadId!, StringComparer.Ordinal);

        public bool AllCurrent { get; set; } = true;
        public bool InsightsCurrent { get; set; } = prepared.Batch is not null;
        public bool ModelEvidenceCurrent { get; set; } = prepared.ModelPreparationCurrent;
        public bool ModelConcernsRetained { get; set; } = true;
        public HashSet<string> ModelIdentities { get; } = new(StringComparer.Ordinal);
        public bool UnavailableNativeEvidence { get; set; }
        public bool DispositionPrepared { get; set; }
        public bool MissPrepared { get; set; }
        public int Changed { get; set; }
        public int Unchanged { get; set; }
        public int SkippedFindings { get; set; }
        public HarvestCoverageState HarvestCoverage { get; } = new();
        public List<RetainedThreadSnapshot> ChangedArchive { get; } = [];
        public List<(DiscussionThreadProgress Progress, string Digest)> ArchiveAcknowledgments { get; } = [];
        public List<(DiscussionThreadProgress Progress, string Digest)> MemoryAcknowledgments { get; } = [];
        public Dictionary<string, string?> StatusUpdates { get; } = new(StringComparer.Ordinal);
    }

    private static PrThreadStatusEntry[] MergeReviewerSummaries(PullRequestThreadSnapshot snapshot, string previousJson)
    {
        var current = snapshot.ReviewerThreads.Select(thread => thread with { CommentHistory = "" });
        if (snapshot.EnumerationComplete)
        {
            return current.ToArray();
        }

        var observed = snapshot.Threads.Select(thread => thread.ThreadId).ToHashSet(StringComparer.Ordinal);
        return (JsonSerializer.Deserialize<PrThreadStatusEntry[]>(previousJson) ?? [])
            .Where(thread => !observed.Contains(thread.ThreadId)).Concat(current).ToArray();
    }

    private PullRequestThreadSnapshotTarget Target(PullRequestSynchronizationRequest request, Guid connectionId)
    {
        var policy = providers.GetReviewSourcePolicy(request.Provider);
        var coordinates = policy.GetCacheCoordinates(request.ProviderScopePath, request.ProviderProjectKey);
        var repository = policy.GetCacheCoordinates(request.ProviderScopePath, request.RepositoryId).ProjectKey;
        return new(
            request.ClientId, connectionId, request.Provider, coordinates.Authority + coordinates.ScopePath,
            coordinates.ProjectKey, repository, request.PullRequestId);
    }

    private async Task<ClientScmConnectionDto?> ResolveConnectionAsync(PullRequestSynchronizationRequest request, CancellationToken ct)
    {
        var operational = request.ConnectionId.HasValue
            ? await connections.GetOperationalConnectionByIdAsync(request.ClientId, request.ConnectionId.Value, ct)
            : await connections.GetOperationalConnectionAsync(request.ClientId, new ProviderHostRef(request.Provider, request.ProviderScopePath), ct);
        if (operational is not null && (operational.ClientId != request.ClientId || operational.ProviderFamily != request.Provider ||
                                        !providers.GetConnectionConfigurationPolicy(request.Provider).MatchesObservedConnectionHost(
                                            operational.HostBaseUrl, request.ProviderScopePath)))
        {
            return null;
        }

        var connection = operational is null ? null : await connections.GetByIdAsync(request.ClientId, operational.Id, ct);
        return connection is { IsActive: true } ? connection : null;
    }
}
