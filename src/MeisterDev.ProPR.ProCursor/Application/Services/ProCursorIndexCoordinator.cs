// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.ProCursor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.ProCursor.Core;

/// <summary>
///     Coordinates durable ProCursor indexing work and refresh requests.
/// </summary>
public sealed partial class ProCursorIndexCoordinator(
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    IProCursorIndexJobRepository indexJobRepository,
    IProCursorIndexSnapshotRepository snapshotRepository,
    IProCursorSymbolGraphRepository symbolGraphRepository,
    IEnumerable<IProCursorMaterializer> materializers,
    IProCursorChunkExtractor chunkExtractor,
    IProCursorEmbeddingService embeddingService,
    IProCursorSymbolExtractor symbolExtractor,
    ILogger<ProCursorIndexCoordinator> logger,
    IOptions<ProCursorOptions> options)
{
    private const int MaxRetryAttempts = 3;

    private const string LeaseExpiredReason =
        "The lease of the ProCursor service that ran the job expired, for example because the service stopped.";

    private readonly ProCursorOptions _options = options.Value;

    /// <summary>
    ///     How long the lease on a running index job lasts. The index worker renews it while the job runs.
    /// </summary>
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(this._options.IndexJobLeaseSeconds);

    /// <summary>
    ///     Claims the next pending job whose source is not already active in the current worker cycle.
    /// </summary>
    /// <returns>
    ///     The claimed job, or null when no job is pending or another service claimed the next job after it was read.
    ///     In the second case, the next worker cycle claims the following job.
    /// </returns>
    public async Task<ProCursorIndexJob?> TryStartNextJobAsync(
        IReadOnlyCollection<Guid> excludedSourceIds,
        CancellationToken ct = default)
    {
        var job = excludedSourceIds.Count == 0
            ? await indexJobRepository.GetNextPendingAsync(ct)
            : await indexJobRepository.GetNextPendingAsync(excludedSourceIds, ct);
        if (job is null)
        {
            return null;
        }

        job.MarkProcessing(DateTimeOffset.UtcNow + this.LeaseDuration);
        return await indexJobRepository.UpdateAsync(job, ct) ? job : null;
    }

    /// <summary>
    ///     Extends the lease on a running job, as long as the job is still in processing under the same claim.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="attemptCount">The attempt count of the claim that the calling service holds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     False when the job was cancelled, ended, or recovered and claimed again. The calling service stops the job
    ///     then.
    /// </returns>
    public async Task<bool> RenewLeaseAsync(Guid jobId, int attemptCount, CancellationToken ct = default)
    {
        var job = await indexJobRepository.GetByIdAsync(jobId, ct);
        if (job is null || job.Status != ProCursorIndexJobStatus.Processing || job.AttemptCount != attemptCount)
        {
            return false;
        }

        job.RenewLease(DateTimeOffset.UtcNow + this.LeaseDuration);
        return await indexJobRepository.UpdateAsync(job, ct);
    }

    /// <summary>
    ///     Returns the jobs whose lease has expired to the queue, or fails them after the last attempt. The service
    ///     that claimed such a job has stopped, or it could not renew the lease for the whole duration and stops the job
    ///     at its next renewal.
    /// </summary>
    /// <param name="runningJobIds">The jobs that the calling service runs itself.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of jobs that were returned to the queue or failed.</returns>
    public async Task<int> RecoverExpiredJobsAsync(IReadOnlyCollection<Guid> runningJobIds, CancellationToken ct = default)
    {
        var recovered = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var job in await indexJobRepository.ListExpiredLeasesAsync(now, runningJobIds, ct))
        {
            if (job.AttemptCount < MaxRetryAttempts)
            {
                job.MarkPendingForRetry(LeaseExpiredReason);
            }
            else
            {
                job.MarkFailed(LeaseExpiredReason);
            }

            if (await indexJobRepository.RecoverExpiredLeaseAsync(job, now, ct))
            {
                recovered++;
                LogExpiredJobRecovered(logger, job.Id, job.AttemptCount, job.Status);
            }
        }

        return recovered;
    }

    /// <summary>
    ///     Executes one previously claimed ProCursor index job.
    /// </summary>
    /// <param name="jobId">The claimed job.</param>
    /// <param name="attemptCount">The attempt count of the claim that the calling service holds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the job is no longer in processing under that claim. The job does not run then.</returns>
    public async Task<bool> ExecuteJobAsync(Guid jobId, int attemptCount, CancellationToken ct = default)
    {
        var job = await indexJobRepository.GetByIdAsync(jobId, ct);
        if (job is null || job.Status != ProCursorIndexJobStatus.Processing || job.AttemptCount != attemptCount)
        {
            // The job was cancelled or recovered between its claim and its start. A job with another attempt count
            // was recovered and claimed again, and the service that holds the newer claim runs it.
            return false;
        }

        var snapshot = default(ProCursorIndexSnapshot);
        var materializedRootDirectory = default(string);

        try
        {
            var source = await knowledgeSourceRepository.GetBySourceIdAsync(job.KnowledgeSourceId, ct)
                         ?? throw new KeyNotFoundException($"ProCursor source {job.KnowledgeSourceId} was not found.");
            var trackedBranch = ResolveTrackedBranch(source, job.TrackedBranchId);
            var materializer = materializers.FirstOrDefault(candidate => candidate.SourceKind == source.SourceKind)
                               ?? throw new InvalidOperationException($"No ProCursor materializer is registered for source kind {source.SourceKind}.");

            await embeddingService.EnsureConfigurationAsync(source.ClientId, ct);

            var materializedSource = await materializer.MaterializeAsync(
                source,
                trackedBranch,
                job.RequestedCommitSha,
                ct);
            materializedRootDirectory = materializedSource.RootDirectory;

            trackedBranch.RecordSeenCommit(materializedSource.CommitSha);
            await knowledgeSourceRepository.UpdateAsync(source, ct);

            var existingSnapshot = await snapshotRepository.GetLatestReadyAsync(source.Id, trackedBranch.Id, ct);
            if (existingSnapshot is not null &&
                string.Equals(
                    existingSnapshot.CommitSha,
                    materializedSource.CommitSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                trackedBranch.RecordIndexedCommit(materializedSource.CommitSha);
                await knowledgeSourceRepository.UpdateAsync(source, ct);
                await this.CompleteAsync(job, ct);
                return true;
            }

            var (normalizedChunks, embeddings) = await this.ExtractAndEmbedChunksAsync(source, materializedSource, job.Id, ct);

            snapshot = new ProCursorIndexSnapshot(
                Guid.NewGuid(),
                source.Id,
                trackedBranch.Id,
                materializedSource.CommitSha,
                "full",
                existingSnapshot?.Id);

            await snapshotRepository.AddAsync(snapshot, ct);

            var knowledgeChunks = BuildKnowledgeChunks(snapshot.Id, normalizedChunks, embeddings);
            await snapshotRepository.ReplaceKnowledgeChunksAsync(snapshot.Id, knowledgeChunks, ct);

            var symbolExtraction = ShouldExtractSymbols(source)
                ? await symbolExtractor.ExtractAsync(materializedSource, snapshot.Id, ct)
                : new ProCursorSymbolExtractionResult([], [], false, "text_only");
            await symbolGraphRepository.ReplaceAsync(snapshot.Id, symbolExtraction.Symbols, symbolExtraction.Edges, ct);

            snapshot.MarkReady(
                materializedSource.MaterializedPaths.Count,
                knowledgeChunks.Count,
                symbolExtraction.Symbols.Count,
                symbolExtraction.SupportsSymbolQueries);
            await snapshotRepository.UpdateAsync(snapshot, ct);

            if (existingSnapshot is not null)
            {
                existingSnapshot.MarkSuperseded();
                await snapshotRepository.UpdateAsync(existingSnapshot, ct);
            }

            trackedBranch.RecordIndexedCommit(materializedSource.CommitSha);
            await knowledgeSourceRepository.UpdateAsync(source, ct);

            await this.CompleteAsync(job, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A cancellation that the caller did not request is a failure of a step, for example an HTTP request that
            // ran into its timeout. A cancellation that the caller requested leaves the job to whoever cancelled it.
            await this.HandleJobFailureAsync(job, snapshot, ex, ct);
            return true;
        }
        finally
        {
            CleanupMaterializedDirectory(materializedRootDirectory);
        }
    }

    /// <summary>
    ///     Extracts source chunks, normalizes them, and generates embeddings for a materialized source,
    ///     validating that every normalized chunk received a corresponding embedding.
    /// </summary>
    private async Task<(IReadOnlyList<ProCursorExtractedChunk> NormalizedChunks, IReadOnlyList<float[]> Embeddings)> ExtractAndEmbedChunksAsync(
        ProCursorKnowledgeSource source,
        ProCursorMaterializedSource materializedSource,
        Guid jobId,
        CancellationToken ct)
    {
        var extractedChunks = await chunkExtractor.ExtractAsync(source, materializedSource, ct);
        var normalizedChunks = extractedChunks.Count == 0
            ? []
            : await embeddingService.NormalizeChunksAsync(source.ClientId, extractedChunks, ct);
        var embeddingUsageContext = normalizedChunks.Count == 0
            ? null
            : new ProCursorEmbeddingUsageContext(
                source.Id,
                source.DisplayName,
                $"pcidx:{jobId:N}",
                ProCursorTokenUsageCallType.Embedding,
                jobId,
                normalizedChunks
                    .Select(chunk => new ProCursorTokenUsageInputContext(chunk.SourcePath))
                    .ToList()
                    .AsReadOnly());
        var embeddings = normalizedChunks.Count == 0
            ? []
            : await embeddingService.GenerateEmbeddingsAsync(
                source.ClientId,
                normalizedChunks.Select(chunk => chunk.ContentText).ToList().AsReadOnly(),
                embeddingUsageContext,
                ct);

        if (embeddings.Count != normalizedChunks.Count)
        {
            throw new InvalidOperationException($"Expected {normalizedChunks.Count} ProCursor embeddings but received {embeddings.Count}.");
        }

        return (normalizedChunks, embeddings);
    }

    /// <summary>
    ///     Records the completion of the job, unless the job was cancelled or recovered while it ran.
    /// </summary>
    private async Task CompleteAsync(ProCursorIndexJob job, CancellationToken ct)
    {
        job.MarkCompleted();
        if (!await indexJobRepository.UpdateAsync(job, ct))
        {
            LogJobChangedWhileRunning(logger, job.Id);
        }
    }

    /// <summary>
    ///     Records a failed job execution: marks any in-progress snapshot as failed, schedules a retry
    ///     or terminal failure for the job depending on the attempt count, and logs the failure.
    /// </summary>
    private async Task HandleJobFailureAsync(
        ProCursorIndexJob job,
        ProCursorIndexSnapshot? snapshot,
        Exception ex,
        CancellationToken ct)
    {
        if (snapshot is not null && string.Equals(snapshot.Status, "building", StringComparison.Ordinal))
        {
            snapshot.MarkFailed(ex.Message);
            await snapshotRepository.UpdateAsync(snapshot, ct);
        }

        if (job.Status != ProCursorIndexJobStatus.Processing)
        {
            // The failure happened while the completion was recorded. The stored job is still in processing, and
            // the index worker recovers it when its lease expires.
            LogCompletionUnrecorded(logger, job.Id, ex);
            return;
        }

        if (job.AttemptCount < MaxRetryAttempts)
        {
            job.MarkPendingForRetry(ex.Message);
        }
        else
        {
            job.MarkFailed(ex.Message);
        }

        if (await indexJobRepository.UpdateAsync(job, ct))
        {
            LogJobFailed(logger, job.Id, ex);
        }
        else
        {
            LogJobChangedWhileRunning(logger, job.Id);
        }
    }

    private static void CleanupMaterializedDirectory(string? materializedRootDirectory)
    {
        if (!string.IsNullOrWhiteSpace(materializedRootDirectory) && Directory.Exists(materializedRootDirectory))
        {
            Directory.Delete(materializedRootDirectory, true);
        }
    }

    /// <summary>
    ///     Queues a refresh or rebuild request for the given source.
    ///     The durable queue implementation is added in a later task.
    /// </summary>
    public async Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await knowledgeSourceRepository.GetByIdAsync(clientId, sourceId, ct);
        if (source is null)
        {
            throw new KeyNotFoundException($"ProCursor source {sourceId} was not found for client {clientId}.");
        }

        var trackedBranch = ResolveTrackedBranch(source, request.TrackedBranchId);
        await embeddingService.EnsureConfigurationAsync(source.ClientId, ct);
        var jobKind = string.IsNullOrWhiteSpace(request.JobKind) ? "refresh" : request.JobKind.Trim();
        var requestedCommitSha = string.IsNullOrWhiteSpace(request.RequestedCommitSha)
            ? null
            : request.RequestedCommitSha.Trim();
        var dedupKey = BuildDedupKey(source.Id, trackedBranch.Id, jobKind, requestedCommitSha);

        if (await indexJobRepository.HasActiveJobAsync(trackedBranch.Id, dedupKey, ct))
        {
            var existing = (await indexJobRepository.ListActiveAsync(source.Id, ct))
                .First(job => job.TrackedBranchId == trackedBranch.Id && job.DedupKey == dedupKey);
            return ToDto(existing, trackedBranch.BranchName);
        }

        var job = new ProCursorIndexJob(
            Guid.NewGuid(),
            source.Id,
            trackedBranch.Id,
            requestedCommitSha,
            jobKind,
            dedupKey);

        await indexJobRepository.AddAsync(job, ct);
        LogRefreshQueued(logger, source.Id, trackedBranch.Id, trackedBranch.BranchName, job.Id, jobKind);
        return ToDto(job, trackedBranch.BranchName);
    }

    private static string BuildDedupKey(Guid sourceId, Guid trackedBranchId, string jobKind, string? requestedCommitSha)
    {
        return string.IsNullOrWhiteSpace(requestedCommitSha)
            ? $"{sourceId:N}:{trackedBranchId:N}:{jobKind}:head"
            : $"{sourceId:N}:{trackedBranchId:N}:{jobKind}:{requestedCommitSha}";
    }

    private static IReadOnlyList<ProCursorKnowledgeChunk> BuildKnowledgeChunks(
        Guid snapshotId,
        IReadOnlyList<ProCursorExtractedChunk> extractedChunks,
        IReadOnlyList<float[]> embeddings)
    {
        var knowledgeChunks = new List<ProCursorKnowledgeChunk>(extractedChunks.Count);

        for (var index = 0; index < extractedChunks.Count; index++)
        {
            var extractedChunk = extractedChunks[index];
            knowledgeChunks.Add(
                new ProCursorKnowledgeChunk(
                    Guid.NewGuid(),
                    snapshotId,
                    extractedChunk.SourcePath,
                    extractedChunk.ChunkKind,
                    extractedChunk.Title,
                    extractedChunk.ChunkOrdinal,
                    extractedChunk.LineStart,
                    extractedChunk.LineEnd,
                    extractedChunk.ContentHash,
                    extractedChunk.ContentText,
                    embeddings[index]));
        }

        return knowledgeChunks.AsReadOnly();
    }

    private static bool ShouldExtractSymbols(ProCursorKnowledgeSource source)
    {
        return source.SourceKind == ProCursorSourceKind.Repository &&
               !string.Equals(source.SymbolMode, "text_only", StringComparison.OrdinalIgnoreCase);
    }

    private static ProCursorTrackedBranch ResolveTrackedBranch(ProCursorKnowledgeSource source, Guid? trackedBranchId)
    {
        if (trackedBranchId.HasValue)
        {
            return source.TrackedBranches.FirstOrDefault(branch => branch.Id == trackedBranchId.Value)
                   ?? throw new KeyNotFoundException($"Tracked branch {trackedBranchId.Value} was not found for source {source.Id}.");
        }

        return source.TrackedBranches.FirstOrDefault(branch =>
                   string.Equals(branch.BranchName, source.DefaultBranch, StringComparison.OrdinalIgnoreCase))
               ?? source.TrackedBranches.FirstOrDefault()
               ?? throw new InvalidOperationException($"Source {source.Id} has no tracked branches.");
    }

    private static ProCursorTrackedBranch ResolveTrackedBranch(ProCursorKnowledgeSource source, Guid trackedBranchId)
    {
        return ResolveTrackedBranch(source, (Guid?)trackedBranchId);
    }

    private static ProCursorIndexJobDto ToDto(ProCursorIndexJob job, string branchName)
    {
        return new ProCursorIndexJobDto(
            job.Id,
            job.KnowledgeSourceId,
            job.TrackedBranchId,
            branchName,
            job.RequestedCommitSha,
            job.JobKind,
            job.Status,
            job.QueuedAt,
            job.StartedAt,
            job.CompletedAt,
            job.FailureReason);
    }
}
