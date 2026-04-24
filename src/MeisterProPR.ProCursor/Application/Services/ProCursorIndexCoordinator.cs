// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

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
    IOptions<ProCursorOptions> options,
    ILicensingCapabilityService? licensingCapabilityService = null)
{
    private const int MaxRetryAttempts = 3;
    private readonly ProCursorOptions _options = options.Value;

    /// <summary>
    ///     Attempts to execute the next queued ProCursor indexing job.
    /// </summary>
    public async Task<bool> ExecuteNextJobAsync(CancellationToken ct = default)
    {
        var job = await this.TryStartNextJobAsync([], ct);
        if (job is null)
        {
            LogIndexCycleHeartbeat(logger, this._options.MaxIndexConcurrency);
            return false;
        }

        await this.ExecuteJobAsync(job.Id, ct);
        return true;
    }

    /// <summary>
    ///     Claims the next pending job whose source is not already active in the current worker cycle.
    /// </summary>
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

        job.MarkProcessing();
        await indexJobRepository.UpdateAsync(job, ct);
        return job;
    }

    /// <summary>
    ///     Executes one previously claimed ProCursor index job.
    /// </summary>
    public async Task<bool> ExecuteJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await indexJobRepository.GetByIdAsync(jobId, ct);
        if (job is null)
        {
            return false;
        }

        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.ProCursor,
            ct);

        if (capability is not null)
        {
            job.MarkFailed(capability.Message ?? $"Capability '{capability.Key}' is unavailable.");
            await indexJobRepository.UpdateAsync(job, ct);
            return true;
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
                job.MarkCompleted();
                await indexJobRepository.UpdateAsync(job, ct);
                return true;
            }

            var extractedChunks = await chunkExtractor.ExtractAsync(source, materializedSource, ct);
            var normalizedChunks = extractedChunks.Count == 0
                ? []
                : await embeddingService.NormalizeChunksAsync(source.ClientId, extractedChunks, ct);
            var embeddingUsageContext = normalizedChunks.Count == 0
                ? null
                : new ProCursorEmbeddingUsageContext(
                    source.Id,
                    source.DisplayName,
                    $"pcidx:{job.Id:N}",
                    ProCursorTokenUsageCallType.Embedding,
                    job.Id,
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

            job.MarkCompleted();
            await indexJobRepository.UpdateAsync(job, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (snapshot is not null && string.Equals(snapshot.Status, "building", StringComparison.Ordinal))
            {
                snapshot.MarkFailed(ex.Message);
                await snapshotRepository.UpdateAsync(snapshot, ct);
            }

            if (job.AttemptCount < MaxRetryAttempts)
            {
                job.MarkPendingForRetry(ex.Message);
            }
            else
            {
                job.MarkFailed(ex.Message);
            }

            await indexJobRepository.UpdateAsync(job, ct);

            LogJobFailed(logger, job.Id, ex);
            return true;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(materializedRootDirectory) && Directory.Exists(materializedRootDirectory))
            {
                Directory.Delete(materializedRootDirectory, true);
            }
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

        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.ProCursor,
            ct);

        if (capability is not null)
        {
            throw new InvalidOperationException(capability.Message ?? $"Capability '{capability.Key}' is unavailable.");
        }

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
