// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Orchestrates thread memory lifecycle (store on resolve, remove on reopen) and
///     memory-augmented AI reconsideration during file review.
///     All public methods must never throw to the caller.
/// </summary>
public sealed partial class ThreadMemoryService(
    IThreadMemoryEmbedder embedder,
    IThreadMemoryRepository repository,
    IProtocolRecorder protocolRecorder,
    IMemoryActivityLog activityLog,
    IOptions<AiReviewOptions> options,
    ILogger<ThreadMemoryService> logger,
    IChatClient? chatClient = null,
    IAiConnectionRepository? aiConnectionRepository = null,
    IAiChatClientFactory? aiChatClientFactory = null,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    ICodeInsightFindingStore? codeInsightFindingStore = null,
    IMemoryKeywordExtractor? memoryKeywordExtractor = null,
    ICodeInsightsCollectionGate? codeInsightsCollectionGate = null) : IThreadMemoryService
{
    private const double HistoricalTextFallbackThreshold = 0.72;
    private const string EmbeddingDegradedComponent = "thread_memory_embedding";
    private const string RepositoryDegradedComponent = "thread_memory_repository";
    private const string FilePathFallbackCheck = "pull_request_file_path_memory";

    /// <summary>
    ///     How many retrievals a job may make, all of them empty, before the emptiness itself is reported.
    ///     A single empty retrieval is ordinary; every retrieval in a job coming back empty while the client
    ///     holds records means the check could not have worked. It also bounds how many files in one job pay
    ///     for the closest-distance probe, so the signal always carries a measured distance.
    /// </summary>
    private const int SustainedEmptyRetrievalAttempts = 5;

    private readonly ConcurrentDictionary<Guid, byte> _embeddingLookupFailuresByClient = new();

    // Keyed by job, not by protocol: a protocol is opened per reviewed file, so a per-protocol tally would
    // only ever count one retrieval. Keying by job also keeps two jobs and two clients apart whatever the
    // lifetime of this instance turns out to be. Files are reviewed in parallel against one instance.
    private readonly ConcurrentDictionary<Guid, EmptyRetrievalTally> _retrievalTalliesByJob = new();
    private readonly ConcurrentDictionary<Guid, int> _clientRecordCountsByJob = new();

    private readonly AiReviewOptions _opts = options.Value;

    /// <inheritdoc />
    public async Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        // Heuristic pre-gate: a thread with no discussion carries no resolution to learn from.
        // Skip before spending any AI call. Threads with any content (including a lone bot finding)
        // fall through to the model, which decides whether a resolution is determinable.
        if (string.IsNullOrWhiteSpace(evt.CommentHistory))
        {
            await this.RecordResolvedSkipAsync(evt, "empty_comment_history", ct);
            return;
        }

        // Grounding gate: a close that only claims a fix must be corroborated by an actual code change
        // before it becomes suppression memory. A thread closed before (or without) the code was updated
        // otherwise teaches a future review to discard a still-valid finding. A deliberate human
        // acceptance (by-design / won't-fix) is itself the resolution and needs no code change.
        if (evt.Intent == ThreadResolutionIntent.ClaimsFix &&
            evt.CodeChangedSinceRaised != ThreadAnchorCodeChange.Changed)
        {
            var groundingReason = evt.CodeChangedSinceRaised == ThreadAnchorCodeChange.Unchanged
                ? "closed_without_code_change"
                : "code_change_undetermined";
            await this.RecordResolvedSkipAsync(evt, groundingReason, ct);
            LogResolvedSkipped(logger, evt.ThreadId, evt.ClientId, groundingReason);
            return;
        }

        // The store keeps one canonical path form, so a record written from a provider thread path stays
        // retrievable by the orchestrator's changed-file path. The two producers disagree: Azure DevOps
        // thread contexts are repository-root-absolute, the rest of the pipeline is repository-relative.
        var canonicalFilePath = CanonicalizeFilePath(evt.FilePath);

        try
        {
            var resolution = await embedder.GenerateResolutionSummaryAsync(
                canonicalFilePath,
                evt.ChangeExcerpt,
                evt.CommentHistory,
                evt.ClientId,
                ct);

            // Clarity gate: only store a genuine, determinable resolution. A code-corroborated fix still
            // must read as a real resolution; a deliberate human acceptance is trusted as long as a real
            // summary was produced (its clarity may read as "closed without resolution" from comments
            // alone even though the human explicitly accepted the concern). A failed summary call is
            // never stored either way.
            var storable = evt.Intent == ThreadResolutionIntent.AcceptedByHuman
                ? resolution.Summary != ThreadResolutionSummary.GenerationFailedSummary
                : resolution.IsStorable;
            if (!storable)
            {
                var reason = ClassifySkipReason(resolution);
                await this.RecordResolvedSkipAsync(evt, reason, ct);
                LogResolvedSkipped(logger, evt.ThreadId, evt.ClientId, reason);
                return;
            }

            var compositeText = BuildCompositeText(
                canonicalFilePath,
                evt.ChangeExcerpt,
                evt.CommentHistory,
                resolution.Summary);
            var vector = await embedder.GenerateEmbeddingAsync(compositeText, evt.ClientId, ct);

            var now = DateTimeOffset.UtcNow;
            var record = new ThreadMemoryRecord
            {
                Id = Guid.NewGuid(),
                ClientId = evt.ClientId,
                ThreadId = evt.ThreadId,
                RepositoryId = evt.RepositoryId,
                PullRequestId = evt.PullRequestId,
                FilePath = canonicalFilePath,
                ChangeExcerpt = TruncateExcerpt(evt.ChangeExcerpt),
                CommentHistoryDigest = evt.CommentHistory,
                ResolutionSummary = resolution.Summary,
                EmbeddingVector = vector,

                // Both classifications decided whether this memory is worth keeping. Keeping them on the
                // record is what lets a later review tell a rejected concern from a repaired one without
                // reading the prose, and tell a crisp acceptance from an inconclusive one. An unresolved
                // intent is stored as absent rather than as itself, so absent stays the one and only way a
                // record says "no reviewer decided anything here".
                ResolutionIntent = evt.Intent == ThreadResolutionIntent.Active ? null : evt.Intent,
                ResolutionClarity = resolution.Clarity,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // Additive code-insight enrichment: the finding this memory came from, and searchable keywords.
            // It sets fields on a record that is already decided and never influences whether the memory is
            // stored, what it says, or how it is later matched.
            await this.EnrichForCodeInsightsAsync(record, evt, ct);

            await repository.UpsertAsync(record, ct);

            await activityLog.AppendAsync(
                new MemoryActivityLogEntry
                {
                    Id = Guid.NewGuid(),
                    ClientId = evt.ClientId,
                    ThreadId = evt.ThreadId,
                    RepositoryId = evt.RepositoryId,
                    PullRequestId = evt.PullRequestId,
                    Action = MemoryActivityAction.Stored,
                    PreviousStatus = null,
                    CurrentStatus = "resolved",
                    Reason = null,
                    OccurredAt = now,
                },
                ct);

            LogEmbeddingStored(logger, evt.ThreadId, evt.ClientId);
        }
        catch (Exception ex)
        {
            // A determinable resolution whose embedding or persistence failed: nothing is stored, so
            // record the skip rather than a phantom "Stored" entry.
            LogProcessResolvedFailed(logger, evt.ThreadId, evt.ClientId, ex);
            await this.RecordResolvedSkipAsync(evt, "embedding_failed", ct);
        }
    }

    /// <summary>
    ///     Attaches code-insight enrichment to a memory record that has already been decided: the finding it
    ///     originated from, and human-searchable keywords.
    /// </summary>
    /// <remarks>
    ///     Strictly additive and entirely best-effort. It runs after every storage and clarity gate has passed,
    ///     sets fields on the record and nothing else, and swallows its own failures: a memory is far more
    ///     valuable than the metadata hanging off it, so enrichment must never be the reason one is lost.
    ///     Both dependencies are optional: absent, or the client's collection gate closed, means no enrichment.
    /// </remarks>
    private async Task EnrichForCodeInsightsAsync(
        ThreadMemoryRecord record,
        ThreadResolvedDomainEvent evt,
        CancellationToken ct)
    {
        if (codeInsightFindingStore is null && memoryKeywordExtractor is null)
        {
            return;
        }

        try
        {
            // Two enrichments with different owners. The finding link is Code Insights data and stays behind its
            // licence and per-client opt-in. Keywords are search metadata on a memory, which is part of the base
            // product, so they are not gated: gating them meant a commercial licence silently improved memory
            // search and its absence silently degraded it, with nothing in the product saying so.
            var collecting = codeInsightsCollectionGate is not null
                             && await codeInsightsCollectionGate.IsCollectionEnabledAsync(evt.ClientId, ct);

            if (collecting && codeInsightFindingStore is not null)
            {
                // Same invariant conversion the disposition path uses: the crawl carries a number, the insight
                // store holds the provider's own string form.
                var finding = await codeInsightFindingStore.FindByProviderThreadAsync(
                    evt.ClientId,
                    evt.RepositoryId,
                    evt.PullRequestId,
                    evt.ThreadId.ToString(CultureInfo.InvariantCulture),
                    ct);

                // Null is the ordinary case for a human thread, an admin dismissal, or a thread raised before
                // collection was enabled. The link simply stays absent.
                record.CodeInsightFindingId = finding?.Id;
            }

            if (memoryKeywordExtractor is not null)
            {
                var keywords = await memoryKeywordExtractor.ExtractAsync(
                    evt.ClientId,
                    record.ResolutionSummary,
                    record.ChangeExcerpt,
                    ct);
                record.Keywords = [.. keywords];
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCodeInsightEnrichmentFailed(logger, evt.ThreadId, evt.ClientId, ex);
        }
    }

    /// <summary>
    ///     Maps a non-storable resolution to an audit reason, distinguishing a failed summary call
    ///     (placeholder text) from a genuine model classification.
    /// </summary>
    private static string ClassifySkipReason(ThreadResolutionSummary resolution)
    {
        if (resolution.Clarity == ResolutionClarity.ClosedWithoutResolution)
        {
            return "closed_without_resolution";
        }

        return resolution.Summary == ThreadResolutionSummary.GenerationFailedSummary
            ? "summary_generation_failed"
            : "undetermined";
    }

    /// <summary>
    ///     Records that a resolved thread was intentionally not stored, as a <c>NoOp</c> activity entry.
    /// </summary>
    private Task RecordResolvedSkipAsync(ThreadResolvedDomainEvent evt, string reason, CancellationToken ct)
    {
        return this.RecordNoOpAsync(
            evt.ClientId,
            evt.RepositoryId,
            evt.PullRequestId,
            evt.ThreadId,
            previousStatus: null,
            currentStatus: "resolved",
            reason: reason,
            ct);
    }

    /// <inheritdoc />
    public async Task HandleThreadReopenedAsync(ThreadReopenedDomainEvent evt, CancellationToken ct = default)
    {
        try
        {
            var deleted = await repository.RemoveByThreadAsync(evt.ClientId, evt.RepositoryId, evt.ThreadId, ct);
            var outcome = deleted ? "deleted" : "no_op";

            await activityLog.AppendAsync(
                new MemoryActivityLogEntry
                {
                    Id = Guid.NewGuid(),
                    ClientId = evt.ClientId,
                    ThreadId = evt.ThreadId,
                    RepositoryId = evt.RepositoryId,
                    PullRequestId = evt.PullRequestId,
                    Action = MemoryActivityAction.Removed,
                    PreviousStatus = "resolved",
                    CurrentStatus = "active",
                    Reason = outcome,
                    OccurredAt = DateTimeOffset.UtcNow,
                },
                ct);

            LogEmbeddingRemoved(logger, evt.ThreadId, evt.ClientId, outcome);
        }
        catch (Exception ex)
        {
            LogProcessReopenedFailed(logger, evt.ThreadId, evt.ClientId, ex);
        }
    }

    /// <inheritdoc />
    public async Task RecordNoOpAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        long threadId,
        string? previousStatus,
        string currentStatus,
        string reason,
        CancellationToken ct = default)
    {
        try
        {
            await activityLog.AppendAsync(
                new MemoryActivityLogEntry
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    ThreadId = threadId,
                    RepositoryId = repositoryId,
                    PullRequestId = pullRequestId,
                    Action = MemoryActivityAction.NoOp,
                    PreviousStatus = previousStatus,
                    CurrentStatus = currentStatus,
                    Reason = reason,
                    OccurredAt = DateTimeOffset.UtcNow,
                },
                ct);
        }
        catch (Exception ex)
        {
            LogRecordNoOpFailed(logger, threadId, clientId, ex);
        }
    }

    /// <inheritdoc />
    public async Task<ThreadMemoryRecord> DismissFindingAsync(
        Guid clientId,
        string? filePath,
        string findingMessage,
        string? label,
        CancellationToken ct = default)
    {
        var canonicalFilePath = CanonicalizeFilePath(filePath);
        var compositeText = canonicalFilePath is null
            ? findingMessage
            : $"{canonicalFilePath}\n{findingMessage}";

        var vector = await embedder.GenerateEmbeddingAsync(compositeText, clientId, ct);

        var now = DateTimeOffset.UtcNow;
        var record = new ThreadMemoryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ThreadId = 0,
            RepositoryId = string.Empty,
            PullRequestId = 0,
            FilePath = canonicalFilePath ?? string.Empty,
            ChangeExcerpt = null,
            CommentHistoryDigest = label ?? findingMessage,
            ResolutionSummary = findingMessage,
            EmbeddingVector = vector,
            MemorySource = MemorySource.AdminDismissed,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.UpsertAsync(record, ct);
        return record;
    }

    /// <inheritdoc />
    public async Task<HistoricalDuplicateSuppressionMatchDto> FindDuplicateSuppressionMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string findingMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(findingMessage) ||
            string.IsNullOrWhiteSpace(repositoryId) ||
            pullRequestId <= 0)
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch();
        }

        var degradedComponents = new HashSet<string>(StringComparer.Ordinal);
        var fallbackChecks = new HashSet<string>(StringComparer.Ordinal);
        var normalizedFilePath = CanonicalizeFilePath(filePath);

        var queryVector = await this.TryGetDuplicateSuppressionQueryVectorAsync(clientId, normalizedFilePath, findingMessage, degradedComponents, ct);

        if (queryVector is not null)
        {
            var semanticMatch = await this.TryFindSemanticDuplicateMatchAsync(clientId, repositoryId, pullRequestId, queryVector, degradedComponents, ct);
            if (semanticMatch is not null)
            {
                return HistoricalDuplicateSuppressionMatchDto.Match(
                    "historical_similarity_match",
                    semanticMatch.ThreadId,
                    semanticMatch.MemoryRecordId,
                    semanticMatch.SimilarityScore,
                    degradedComponents.ToList().AsReadOnly(),
                    BuildDuplicateSuppressionDegradedCause(degradedComponents),
                    fallbackChecks.ToList().AsReadOnly());
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedFilePath))
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch(
                degradedComponents.ToList().AsReadOnly(),
                BuildDuplicateSuppressionDegradedCause(degradedComponents),
                fallbackChecks.ToList().AsReadOnly());
        }

        var fallbackMatch = await this.TryFindFilePathFallbackMatchAsync(
            new FilePathFallbackInputs(clientId, repositoryId, pullRequestId, normalizedFilePath, findingMessage, degradedComponents, fallbackChecks, ct));
        if (fallbackMatch is not null)
        {
            return HistoricalDuplicateSuppressionMatchDto.Match(
                "historical_similarity_match",
                fallbackMatch.Match.ThreadId,
                fallbackMatch.Match.MemoryRecordId,
                (float)fallbackMatch.Score,
                degradedComponents.ToList().AsReadOnly(),
                BuildDuplicateSuppressionDegradedCause(degradedComponents),
                fallbackChecks.ToList().AsReadOnly());
        }

        return HistoricalDuplicateSuppressionMatchDto.NoMatch(
            degradedComponents.ToList().AsReadOnly(),
            BuildDuplicateSuppressionDegradedCause(degradedComponents),
            fallbackChecks.ToList().AsReadOnly());
    }

    /// <inheritdoc />
    public async Task<ReviewResult> RetrieveAndReconsiderAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct = default,
        float? temperature = null)
    {
        try
        {
            var retrieval = await this.RetrieveMemoryMatchesAsync(
                clientId,
                job,
                filePath,
                changeExcerpt,
                draftResult,
                protocolId,
                ct);
            var matches = retrieval.Matches;

            await this.RecordRetrievalProtocolAsync(protocolId, filePath, retrieval, ct);
            await this.SignalSustainedEmptyRetrievalAsync(protocolId, job.Id, retrieval, ct);

            if (matches.Count == 0)
            {
                return draftResult;
            }

            var effectiveModelId = job.AiModel ?? this._opts.ModelId;
            var reconsideredResult = await this.ReconsiderWithAiAsync(
                clientId,
                draftResult,
                matches,
                effectiveModelId,
                protocolId,
                temperature,
                ct);

            await this.RecordReconsiderationProtocolAsync(protocolId, clientId, filePath, draftResult, reconsideredResult, matches, ct);

            return reconsideredResult ?? draftResult;
        }
        catch (Exception ex)
        {
            LogRetrieveAndReconsiderFailed(logger, filePath, clientId, ex);
            await this.RecordOperationFailedProtocolAsync(protocolId, filePath, ex, ct);
            return draftResult;
        }
    }

    private async Task<MemoryRetrieval> RetrieveMemoryMatchesAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct)
    {
        var lookupFilePath = CanonicalizeFilePath(filePath);

        // Build query composite text from file path + change excerpt + draft findings summary.
        var queryText = BuildQueryText(lookupFilePath, changeExcerpt, draftResult.Summary);
        var queryVector = await embedder.GenerateEmbeddingAsync(queryText, clientId, ct);

        var semanticMatches = await repository.FindSimilarAsync(
            clientId,
            queryVector,
            this._opts.MemoryTopN,
            this._opts.MemoryMinSimilarity,
            ct) ?? [];

        if (semanticMatches.Count > 0)
        {
            return new MemoryRetrieval(
                PreferDecidedOutcomes(semanticMatches),
                "semantic_similarity",
                lookupFilePath,
                null);
        }

        IReadOnlyList<ThreadMemoryMatchDto> exactFileMatches = lookupFilePath is null
            ? []
            : await repository.FindByFilePathAsync(
                clientId,
                job.RepositoryId,
                lookupFilePath,
                this._opts.MemoryTopN,
                ct) ?? [];

        if (exactFileMatches.Count > 0)
        {
            return new MemoryRetrieval(
                PreferDecidedOutcomes(exactFileMatches),
                "exact_file_fallback",
                lookupFilePath,
                null);
        }

        var emptiness = protocolId.HasValue
            ? await this.DescribeEmptyRetrievalAsync(clientId, job.Id, queryVector, ct)
            : null;

        return new MemoryRetrieval(semanticMatches, "no_match", lookupFilePath, emptiness);
    }

    /// <summary>
    ///     Explains an empty retrieval with the two facts that decide whether it could have matched at all:
    ///     how many records the client holds, which is the population the similarity search covers, and how
    ///     close the closest of them came. Purely descriptive and best-effort, so a failed probe leaves the
    ///     retrieval itself untouched.
    /// </summary>
    /// <remarks>
    ///     Both probes are bounded per job, because this runs on the review hot path for every file that
    ///     finds nothing. The count is asked once and reused, since it does not move while a job runs. The
    ///     distance probe repeats the search that just ran with the threshold removed, so it costs one more
    ///     vector query, and only the first few files in a job pay it: a sample answers the question, and a
    ///     large pull request must not double its vector queries to produce one diagnostic.
    /// </remarks>
    private async Task<MemoryRetrievalEmptiness?> DescribeEmptyRetrievalAsync(
        Guid clientId,
        Guid jobId,
        float[] queryVector,
        CancellationToken ct)
    {
        try
        {
            if (!this._clientRecordCountsByJob.TryGetValue(jobId, out var recordsForClient))
            {
                var page = await repository.GetPagedAsync(clientId, null, 1, 1, null, null, null, ct);
                if (page is null)
                {
                    return null;
                }

                recordsForClient = page.TotalCount;
                this._clientRecordCountsByJob[jobId] = recordsForClient;
            }

            if (recordsForClient == 0)
            {
                return new MemoryRetrievalEmptiness(0, null);
            }

            var tally = this._retrievalTalliesByJob.GetOrAdd(jobId, _ => new EmptyRetrievalTally());
            if (!tally.TryClaimDistanceProbe(SustainedEmptyRetrievalAttempts))
            {
                return new MemoryRetrievalEmptiness(recordsForClient, null);
            }

            var closest = await repository.FindSimilarAsync(clientId, queryVector, 1, 0f, ct);
            return new MemoryRetrievalEmptiness(
                recordsForClient,
                closest is { Count: > 0 } ? closest[0].SimilarityScore : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEmptyRetrievalProbeFailed(logger, clientId, ex);
            return null;
        }
    }

    /// <summary>
    ///     Reports an empty-retrieval rate that has stopped being plausible. An empty retrieval is a healthy
    ///     zero on its own, so nothing about a single one is worth an event; every retrieval in a job coming
    ///     back empty while the client holds records is not. The tally is the job's, because a protocol is
    ///     opened per file, and the event is attached to whichever file's protocol crossed the line.
    /// </summary>
    private async Task SignalSustainedEmptyRetrievalAsync(
        Guid? protocolId,
        Guid jobId,
        MemoryRetrieval retrieval,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var tally = this._retrievalTalliesByJob.GetOrAdd(jobId, _ => new EmptyRetrievalTally());
        var (attempts, emptyResults) = tally.Record(retrieval.Matches.Count == 0);

        if (attempts < SustainedEmptyRetrievalAttempts ||
            emptyResults < attempts ||
            retrieval.Emptiness is null ||
            retrieval.Emptiness.RecordsForClient == 0)
        {
            return;
        }

        if (!tally.TryClaimSignal())
        {
            return;
        }

        var details = JsonSerializer.Serialize(
            new
            {
                reason = "sustained_empty_retrieval",
                attempts,
                emptyResults,
                recordsForClient = retrieval.Emptiness.RecordsForClient,
                closestAvailableSimilarity = retrieval.Emptiness.ClosestAvailableSimilarity,
                minSimilarity = this._opts.MemoryMinSimilarity,
            });

        LogSustainedEmptyRetrieval(logger, attempts, retrieval.Emptiness.RecordsForClient);
        await protocolRecorder.RecordMemoryEventAsync(
            protocolId.Value,
            "memory_retrieval_degraded",
            details,
            null,
            ct);
    }

    private async Task RecordRetrievalProtocolAsync(
        Guid? protocolId,
        string filePath,
        MemoryRetrieval retrieval,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var matches = retrieval.Matches;
        var similarityScores = matches.Select(m => m.SimilarityScore).ToList();
        var retrievalDetails = JsonSerializer.Serialize(
            new
            {
                filePath,
                lookupFilePath = retrieval.LookupFilePath,
                resultCount = matches.Count,
                topN = this._opts.MemoryTopN,
                minSimilarity = this._opts.MemoryMinSimilarity,
                retrievalMode = retrieval.RetrievalMode,
                recordsForClient = retrieval.Emptiness?.RecordsForClient,
                closestAvailableSimilarity = retrieval.Emptiness?.ClosestAvailableSimilarity,
                similarityScores,
                // In the order the model reads them, with the outcome that decided that order, so the trace
                // explains why a memory came first instead of leaving it to be inferred from the scores.
                matchSources = matches.Select(m => new
                    {
                        m.MemoryRecordId,
                        m.ThreadId,
                        m.FilePath,
                        m.SimilarityScore,
                        m.MatchSource,
                        resolutionIntent = m.Intent?.ToString(),
                        resolutionClarity = m.Clarity?.ToString(),
                    })
                    .ToList(),
            });
        await protocolRecorder.RecordMemoryEventAsync(
            protocolId.Value,
            "memory_retrieval_executed",
            retrievalDetails,
            null,
            ct);
    }

    private async Task RecordReconsiderationProtocolAsync(
        Guid? protocolId,
        Guid clientId,
        string filePath,
        ReviewResult draftResult,
        ReviewResult? reconsideredResult,
        IReadOnlyList<ThreadMemoryMatchDto> matches,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        if (reconsideredResult is not null)
        {
            var (discarded, downgraded) = ComputeReconsiderationDiff(draftResult, reconsideredResult);
            var reconsiderationDetails = JsonSerializer.Serialize(
                new
                {
                    filePath,
                    contributingMemoryIds = matches.Select(m => m.MemoryRecordId).ToList(),
                    originalCommentCount = draftResult.Comments.Count,
                    finalCommentCount = reconsideredResult.Comments.Count,
                    retainedCount = reconsideredResult.Comments.Count - downgraded.Count,
                    discardedCount = discarded.Count,
                    downgradedCount = downgraded.Count,
                    discarded,
                    downgraded,
                });
            await protocolRecorder.RecordMemoryEventAsync(
                protocolId.Value,
                "memory_reconsideration_completed",
                reconsiderationDetails,
                null,
                ct);
            return;
        }

        // AI returned null (empty response, parse failure, or inner exception).
        // Emit a protocol event so the trace is not left dangling after memory_retrieval_executed.
        var failureDetails = JsonSerializer.Serialize(
            new
            {
                filePath,
                contributingMemoryIds = matches.Select(m => m.MemoryRecordId).ToList(),
                originalCommentCount = draftResult.Comments.Count,
                reason = "ai_returned_null_or_parse_failed",
            });
        await protocolRecorder.RecordMemoryEventAsync(
            protocolId.Value,
            "memory_reconsideration_failed",
            failureDetails,
            "Reconsideration AI call returned no usable result; draft findings retained unchanged.",
            ct);
        LogReconsiderationFallback(logger, filePath, clientId);
    }

    private static (IReadOnlyList<object> Discarded, IReadOnlyList<object> Downgraded) ComputeReconsiderationDiff(
        ReviewResult draftResult,
        ReviewResult reconsideredResult)
    {
        var reconsideredKeys = new HashSet<string>(reconsideredResult.Comments.Select(CommentKey));
        var discarded = draftResult.Comments
            .Where(c => !reconsideredKeys.Contains(CommentKey(c)))
            .Select(c => new
            {
                filePath = c.FilePath,
                lineNumber = c.LineNumber,
                severity = c.Severity.ToString().ToLowerInvariant(),
                message = Truncate(c.Message),
            })
            .ToList();

        var draftSeverityByKey = new Dictionary<string, CommentSeverity>();
        foreach (var c in draftResult.Comments)
        {
            draftSeverityByKey.TryAdd(CommentKey(c), c.Severity);
        }

        var downgraded = reconsideredResult.Comments
            .Where(c => draftSeverityByKey.TryGetValue(CommentKey(c), out var origSev) && origSev != c.Severity)
            .Select(c =>
            {
                draftSeverityByKey.TryGetValue(CommentKey(c), out var origSev);
                return new
                {
                    filePath = c.FilePath,
                    lineNumber = c.LineNumber,
                    originalSeverity = origSev.ToString().ToLowerInvariant(),
                    newSeverity = c.Severity.ToString().ToLowerInvariant(),
                    message = Truncate(c.Message),
                };
            })
            .ToList();

        return (discarded, downgraded);
    }

    private static string CommentKey(ReviewComment c)
    {
        var msg = c.Message ?? string.Empty;
        return $"{c.FilePath}:{c.LineNumber}:{(msg.Length > 80 ? msg[..80] : msg)}";
    }

    private static string Truncate(string? message)
    {
        var msg = message ?? string.Empty;
        return msg.Length > 80 ? msg[..80] : msg;
    }

    private async Task RecordOperationFailedProtocolAsync(
        Guid? protocolId,
        string filePath,
        Exception ex,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var details = JsonSerializer.Serialize(new { filePath, operationType = "retrieve_and_reconsider" });
        await protocolRecorder.RecordMemoryEventAsync(
            protocolId.Value,
            "memory_operation_failed",
            details,
            ex.Message,
            ct);
    }

    // Resolves the query embedding used for semantic duplicate-suppression lookup, marking the
    // embedding component degraded (and short-circuiting future calls for this client for the
    // remainder of the process) on failure.
    private async Task<float[]?> TryGetDuplicateSuppressionQueryVectorAsync(
        Guid clientId,
        string? normalizedFilePath,
        string findingMessage,
        HashSet<string> degradedComponents,
        CancellationToken ct)
    {
        if (this._embeddingLookupFailuresByClient.ContainsKey(clientId))
        {
            degradedComponents.Add(EmbeddingDegradedComponent);
            return null;
        }

        try
        {
            var queryText = BuildDuplicateSuppressionQueryText(normalizedFilePath, findingMessage);
            return await embedder.GenerateEmbeddingAsync(queryText, clientId, ct);
        }
        catch (Exception ex)
        {
            this._embeddingLookupFailuresByClient.TryAdd(clientId, 0);
            degradedComponents.Add(EmbeddingDegradedComponent);
            LogDuplicateSuppressionEmbeddingFailed(logger, normalizedFilePath, clientId, ex);
            return null;
        }
    }

    private async Task<ThreadMemoryMatchDto?> TryFindSemanticDuplicateMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        HashSet<string> degradedComponents,
        CancellationToken ct)
    {
        try
        {
            var semanticMatches = await repository.FindSimilarInPullRequestAsync(
                clientId,
                repositoryId,
                pullRequestId,
                queryVector,
                this._opts.MemoryTopN,
                this._opts.MemoryMinSimilarity,
                ct);

            return semanticMatches
                .OrderByDescending(match => match.SimilarityScore)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            degradedComponents.Add(RepositoryDegradedComponent);
            LogDuplicateSuppressionLookupFailed(logger, repositoryId, pullRequestId, clientId, ex);
            return null;
        }
    }

    private async Task<TextSimilarityMatch?> TryFindFilePathFallbackMatchAsync(FilePathFallbackInputs inputs)
    {
        try
        {
            var filePathMatches = await repository.FindByPullRequestFilePathAsync(
                inputs.ClientId,
                inputs.RepositoryId,
                inputs.PullRequestId,
                inputs.NormalizedFilePath,
                this._opts.MemoryTopN,
                inputs.Ct);

            if (inputs.DegradedComponents.Count > 0)
            {
                inputs.FallbackChecks.Add(FilePathFallbackCheck);
            }

            return filePathMatches
                .Select(match => new TextSimilarityMatch(match, CalculateTextSimilarity(inputs.FindingMessage, match.ResolutionSummary)))
                .Where(candidate => candidate.Score >= HistoricalTextFallbackThreshold)
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            inputs.DegradedComponents.Add(RepositoryDegradedComponent);
            LogDuplicateSuppressionLookupFailed(logger, inputs.RepositoryId, inputs.PullRequestId, inputs.ClientId, ex);
            return null;
        }
    }

    private async Task<ReviewResult?> ReconsiderWithAiAsync(
        Guid clientId,
        ReviewResult draftResult,
        IReadOnlyList<ThreadMemoryMatchDto> matches,
        string modelId,
        Guid? protocolId,
        float? temperature,
        CancellationToken ct)
    {
        var resolved = await this.ResolveReconsiderationChatClientAsync(clientId, modelId, ct);
        if (resolved is null)
        {
            return null;
        }

        try
        {
            var (systemMsg, userMsg) = this.BuildReconsiderationMessages(draftResult, matches);

            var response = await resolved.Value.ChatClient.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, systemMsg),
                    new ChatMessage(ChatRole.User, userMsg),
                },
                new ChatOptions { ModelId = resolved.Value.ModelId, Temperature = temperature },
                ct);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            await this.RecordReconsiderationProtocolIfNeededAsync(
                protocolId,
                resolved.Value.ModelId,
                resolved.Value.LogicalModelName,
                systemMsg,
                userMsg,
                text,
                response,
                ct);

            return ParseReconsiderationResponse(text, draftResult);
        }
        catch (Exception ex)
        {
            LogReconsiderationAiFailed(logger, ex);
            return null;
        }
    }

    private async Task<ResolvedChatClient?> ResolveReconsiderationChatClientAsync(
        Guid clientId,
        string modelId,
        CancellationToken ct)
    {
        if (chatClient is not null)
        {
            return new ResolvedChatClient(chatClient, modelId);
        }

        if (aiRuntimeResolver is not null)
        {
            var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(
                clientId,
                AiPurpose.MemoryReconsideration,
                ct);
            return new ResolvedChatClient(runtime.ChatClient, runtime.Model.RemoteModelId, runtime.LogicalModelName);
        }

        if (aiConnectionRepository is null || aiChatClientFactory is null)
        {
            return null;
        }

        var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(clientId, ct);
        if (activeConnection is null)
        {
            return null;
        }

        var resolvedModelId = activeConnection.GetBoundModelId(AiPurpose.MemoryReconsideration)
                              ?? activeConnection.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId
                              ?? modelId;
        var client = aiChatClientFactory.CreateClient(activeConnection.BaseUrl, activeConnection.Secret);
        return new ResolvedChatClient(client, resolvedModelId);
    }

    private (string SystemMessage, string UserMessage) BuildReconsiderationMessages(
        ReviewResult draftResult,
        IReadOnlyList<ThreadMemoryMatchDto> matches)
    {
        var draftJson = JsonSerializer.Serialize(
            new
            {
                summary = draftResult.Summary,
                comments = draftResult.Comments.Select(c => new
                {
                    file_path = c.FilePath,
                    line_number = c.LineNumber,
                    severity = c.Severity.ToString().ToLowerInvariant(),
                    message = c.Message,
                }),
            });

        return (BuildReconsiderationSystemPrompt(), BuildReconsiderationUserMessage(draftJson, matches));
    }

    private async Task RecordReconsiderationProtocolIfNeededAsync(
        Guid? protocolId,
        string effectiveModelId,
        string? logicalModelName,
        string systemMsg,
        string userMsg,
        string text,
        ChatResponse response,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        // Record the reconsideration AI call in the protocol for full token traceability.
        var usage = AiTokenUsageExtractor.FromResponse(response);
        var inputTokens = response.Usage?.InputTokenCount;
        var outputTokens = response.Usage?.OutputTokenCount;
        await protocolRecorder.RecordAiCallAsync(
            protocolId.Value,
            0,
            inputTokens,
            outputTokens,
            userMsg,
            systemMsg,
            text,
            ct,
            "ai_call_memory_reconsideration",
            cachedInputTokens: usage.IsEstimated ? null : usage.CachedInputTokens,
            cacheWriteTokens: usage.IsEstimated ? null : usage.CacheWriteTokens,
            reasoningTokens: usage.IsEstimated ? null : usage.ReasoningTokens);
        await protocolRecorder.AddTokensAsync(
            protocolId.Value,
            usage.InputTokens,
            usage.OutputTokens,
            AiConnectionModelCategory.MemoryReconsideration,
            effectiveModelId,
            ct,
            usage.CachedInputTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens,
            logicalModelName);
    }

    private readonly record struct ResolvedChatClient(IChatClient ChatClient, string ModelId, string? LogicalModelName = null);

    private static string BuildReconsiderationSystemPrompt()
    {
        return """
               You are an expert code reviewer with access to historical memory of past PR review decisions.
               You are in the RECONSIDERATION phase — you will be given draft findings from an initial review pass
               alongside records of how similar issues were resolved previously in this codebase.

               Your task: evaluate each draft finding against the historical context and decide whether to:
                 - RETAIN: The current finding is valid even considering past resolutions (same problem recurs unfixed, or a different instance).
                 - DOWNGRADE: Lower the severity if history shows the team typically accepts this pattern.
                 - DISCARD: Remove the finding if a past resolution clearly demonstrates the same concern was intentionally accepted or by design.

               CRITICAL OUTPUT RULE: Your ENTIRE response must be a single raw JSON object using exactly these keys:
                 "summary" (string), "comments" (array with file_path/line_number/severity/message),
                 "confidence_evaluations" (array), "investigation_complete" (bool), "loop_complete" (bool).
               Do NOT wrap in markdown fences. Return only valid JSON.
               """;
    }

    private static string BuildReconsiderationUserMessage(
        string draftFindingsJson,
        IReadOnlyList<ThreadMemoryMatchDto> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Draft Findings from Initial Review");
        sb.AppendLine(draftFindingsJson);
        sb.AppendLine();
        sb.AppendLine("## Historical Memory — Past Resolved Threads and Dismissed Patterns");

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var isDismissed = m.Source == MemorySource.AdminDismissed;

            if (isDismissed)
            {
                sb.AppendLine($"### Entry {i + 1} ⚠️ ADMIN-DISMISSED PATTERN (Memory ID: {m.MemoryRecordId})");
                sb.AppendLine("  The administrator has explicitly dismissed this pattern. **DISCARD** any finding that closely matches it.");
            }
            else if (string.Equals(m.MatchSource, "exact_file_fallback", StringComparison.Ordinal))
            {
                sb.AppendLine($"### Entry {i + 1} (Exact file fallback, Memory ID: {m.MemoryRecordId})");
            }
            else
            {
                sb.AppendLine($"### Entry {i + 1} (Similarity: {m.SimilarityScore:F2}, Memory ID: {m.MemoryRecordId})");
            }

            if (m.FilePath is not null)
            {
                sb.AppendLine($"- **File**: {m.FilePath}");
            }

            if (!isDismissed && DescribeReviewerOutcome(m) is { } outcome)
            {
                sb.AppendLine($"- **Reviewer outcome**: {outcome}");
            }

            sb.AppendLine(
                isDismissed
                    ? $"- **Dismissed pattern**: {m.ResolutionSummary}"
                    : $"- **How it was resolved**: {m.ResolutionSummary}");
            sb.AppendLine();
        }

        sb.AppendLine("Reconsider the draft findings above using the historical context. Return your reconsidered findings as a JSON object.");
        return sb.ToString();
    }

    /// <summary>
    ///     States what the reviewer's resolution meant, so the model is not left inferring it from prose. A
    ///     rejection and a fix imply opposite actions on a recurrence, and an acceptance the discussion never
    ///     made explicit is worth less than one it did. Returns <see langword="null" /> for a record that
    ///     carries no reviewer outcome, so nothing is claimed about it.
    /// </summary>
    private static string? DescribeReviewerOutcome(ThreadMemoryMatchDto match)
    {
        // Only an explicit acceptance without a code change, reached by a content match, reads as a decision
        // the reviewer stated plainly. Every other clarity, including one this code does not know, and every
        // memory pulled in only because it sits on the same file, is reported as the weaker signal. A new
        // clarity value or a file-level coincidence can therefore never become an instruction to drop a
        // finding.
        var matchedOnContent = !string.Equals(match.MatchSource, "exact_file_fallback", StringComparison.Ordinal);

        return match switch
        {
            { Intent: ThreadResolutionIntent.AcceptedByHuman, Clarity: ResolutionClarity.AcceptedWithoutChange }
                when matchedOnContent =>
                "A reviewer rejected this concern and accepted the code as it stands. **DISCARD** a draft "
                + "finding that raises this same concern about this same code.",

            // Two different weaknesses, and saying the wrong one gives the model a false reason to trust or
            // distrust the record. A file-path match is a weak link to the draft finding; an unclear
            // discussion is a weak decision even when the match itself is exact.
            { Intent: ThreadResolutionIntent.AcceptedByHuman } when !matchedOnContent =>
                "A reviewer rejected a concern in this file and accepted the code as it stands, but this "
                + "record was found only because it sits on the same file, not because it matches this "
                + "finding. Treat it as low confidence and weigh it against the code you see.",
            { Intent: ThreadResolutionIntent.AcceptedByHuman } =>
                "A reviewer rejected this concern and accepted the code as it stands, but the discussion did "
                + "not say so plainly. Treat the decision as low confidence and weigh it against the code "
                + "you see.",
            { Intent: ThreadResolutionIntent.ClaimsFix } =>
                "A reviewer marked a concern here fixed and the code at that spot changed. That is not a "
                + "decision to accept the concern, so on its own it does not justify discarding a finding "
                + "that raises it again.",
            _ => null,
        };
    }

    private static ReviewResult? ParseReconsiderationResponse(string json, ReviewResult draftResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var summaryEl)
                ? summaryEl.GetString() ?? draftResult.Summary
                : draftResult.Summary;

            var comments = new List<ReviewComment>();
            if (root.TryGetProperty("comments", out var commentsEl))
            {
                foreach (var commentEl in commentsEl.EnumerateArray())
                {
                    comments.Add(ParseReconsideredComment(commentEl));
                }
            }

            return new ReviewResult(summary, comments.AsReadOnly());
        }
        catch
        {
            return null;
        }
    }

    private static ReviewComment ParseReconsideredComment(JsonElement commentEl)
    {
        var filePath = commentEl.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        int? lineNumber = commentEl.TryGetProperty("line_number", out var ln) &&
                          ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null;
        var severityStr = commentEl.TryGetProperty("severity", out var sv) ? sv.GetString() : "warning";
        var message = commentEl.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        var severity = Enum.TryParse<CommentSeverity>(severityStr, true, out var parsed)
            ? parsed
            : CommentSeverity.Warning;

        return new ReviewComment(filePath, lineNumber, severity, message);
    }

    private static string BuildCompositeText(
        string? filePath,
        string? changeExcerpt,
        string commentHistory,
        string resolutionSummary)
    {
        return $"File: {filePath ?? "(PR level)"}\n" +
               $"Change: {TruncateExcerpt(changeExcerpt) ?? "(not available)"}\n" +
               $"Comments: {commentHistory}\n" +
               $"Resolution: {resolutionSummary}";
    }

    private static string BuildQueryText(string? filePath, string? changeExcerpt, string draftSummary)
    {
        // The same absent-path wording the stored composite uses, so the two documents stay comparable.
        return $"File: {filePath ?? "(PR level)"}\n" +
               $"Change: {TruncateExcerpt(changeExcerpt) ?? "(not available)"}\n" +
               $"Finding: {draftSummary}";
    }

    private static string BuildDuplicateSuppressionQueryText(string? filePath, string findingMessage)
    {
        return $"File: {filePath ?? "(PR level)"}\n" +
               $"Finding: {findingMessage}";
    }

    /// <summary>
    ///     Puts the memories that record a decision in front of the ones that record a repair. An
    ///     administrator dismissal and a reviewer's acceptance both say "this concern is settled here", which
    ///     is what a recurrence has to be judged against; a claimed fix says the opposite, that the code
    ///     moved. The sort is stable, so similarity still orders each group. It reorders what the model reads
    ///     first within an already-retrieved set, and does not change which records the store returned.
    /// </summary>
    private static IReadOnlyList<ThreadMemoryMatchDto> PreferDecidedOutcomes(IReadOnlyList<ThreadMemoryMatchDto> matches)
    {
        return matches.Count < 2
            ? matches
            : matches.OrderBy(RankOutcome).ToList().AsReadOnly();

        static int RankOutcome(ThreadMemoryMatchDto match)
        {
            // An unrecorded outcome ranks between the two, never with the fixes. Records written before the
            // outcome was kept hold rejections and fixes alike, and demoting them all as if they were fixes
            // would push a genuine past rejection out of the model's view on every install that has history.
            return match switch
            {
                { Source: MemorySource.AdminDismissed } => 0,
                { Intent: ThreadResolutionIntent.AcceptedByHuman } => 1,
                { Intent: null } => 2,
                _ => 3,
            };
        }
    }

    /// <summary>
    ///     Reduces a file path to the one form the memory store keeps: repository-relative, forward slashes,
    ///     no leading slash. Every write and every read goes through this, so a record is retrievable by
    ///     whichever producer supplies the path. Returns <see langword="null" /> for a path that carries no
    ///     file, which is what a pull-request-level thread has.
    /// </summary>
    private static string? CanonicalizeFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var canonical = filePath.Replace('\\', '/').Trim().TrimStart('/');
        return canonical.Length == 0 ? null : canonical;
    }

    private static string? BuildDuplicateSuppressionDegradedCause(IReadOnlyCollection<string> degradedComponents)
    {
        if (degradedComponents.Count == 0)
        {
            return null;
        }

        if (degradedComponents.Contains(EmbeddingDegradedComponent) &&
            degradedComponents.Contains(RepositoryDegradedComponent))
        {
            return "Historical duplicate protection ran without thread-memory embeddings or repository lookups.";
        }

        if (degradedComponents.Contains(EmbeddingDegradedComponent))
        {
            return "Historical duplicate protection ran without thread-memory embeddings.";
        }

        return "Historical duplicate protection ran without thread-memory repository lookups.";
    }

    private static double CalculateTextSimilarity(string first, string second)
    {
        var firstTokens = TokenizeForSimilarity(first);
        var secondTokens = TokenizeForSimilarity(second);

        if (firstTokens.Count == 0 || secondTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = firstTokens.Intersect(secondTokens).Count();
        var union = firstTokens.Union(secondTokens).Count();
        return union == 0 ? 0d : (double)intersection / union;
    }

    private static HashSet<string> TokenizeForSimilarity(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;

        for (var index = 0; index <= text.Length; index++)
        {
            var isWordChar = index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '\'');
            if (isWordChar)
            {
                continue;
            }

            if (index > start)
            {
                var token = text[start..index].ToLowerInvariant();
                if (token.Length >= 3)
                {
                    tokens.Add(token);
                }
            }

            start = index + 1;
        }

        return tokens;
    }

    private static string? TruncateExcerpt(string? text, int maxLength = 2000)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private sealed record TextSimilarityMatch(ThreadMemoryMatchDto Match, double Score);

    /// <summary>
    ///     One review-time retrieval: what it found, which route found it, the canonical path it looked up,
    ///     and, when it found nothing, what it had to work with.
    /// </summary>
    private sealed record MemoryRetrieval(
        IReadOnlyList<ThreadMemoryMatchDto> Matches,
        string RetrievalMode,
        string? LookupFilePath,
        MemoryRetrievalEmptiness? Emptiness);

    /// <summary>
    ///     What an empty retrieval considered: the records the client holds, which is the population the
    ///     similarity search covers, and the highest similarity any of them reached without having to clear
    ///     the configured threshold. That similarity is absent when the probe was not run for this file, or
    ///     when nothing in the corpus reached even zero similarity.
    /// </summary>
    private sealed record MemoryRetrievalEmptiness(int RecordsForClient, float? ClosestAvailableSimilarity);

    /// <summary>
    ///     One job's retrieval tally. Both counters move together under one lock, so the pair a caller
    ///     observes is a consistent snapshot even while files are retrieved in parallel, and both the signal
    ///     and the bounded distance-probe budget are claimed at most once each.
    /// </summary>
    private sealed class EmptyRetrievalTally
    {
        private readonly object _gate = new();
        private int _attempts;
        private int _emptyResults;
        private int _distanceProbes;
        private bool _signalled;

        public (int Attempts, int EmptyResults) Record(bool empty)
        {
            lock (this._gate)
            {
                this._attempts++;
                if (empty)
                {
                    this._emptyResults++;
                }

                return (this._attempts, this._emptyResults);
            }
        }

        public bool TryClaimSignal()
        {
            lock (this._gate)
            {
                if (this._signalled)
                {
                    return false;
                }

                this._signalled = true;
                return true;
            }
        }

        public bool TryClaimDistanceProbe(int budget)
        {
            lock (this._gate)
            {
                if (this._distanceProbes >= budget)
                {
                    return false;
                }

                this._distanceProbes++;
                return true;
            }
        }
    }

    private sealed record FilePathFallbackInputs(
        Guid ClientId,
        string RepositoryId,
        int PullRequestId,
        string NormalizedFilePath,
        string FindingMessage,
        HashSet<string> DegradedComponents,
        HashSet<string> FallbackChecks,
        CancellationToken Ct);
}
