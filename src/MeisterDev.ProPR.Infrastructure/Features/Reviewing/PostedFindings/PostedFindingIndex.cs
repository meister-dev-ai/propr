// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.PostedFindings;

/// <summary>
///     Embedding-backed implementation of the posted-finding index.
/// </summary>
/// <remarks>
///     Neither entry point throws. Duplicate protection is an optimisation on top of a review, so when it
///     cannot run the review still publishes and the gap is reported as degraded. Losing a duplicate costs a
///     reviewer one dismissal; losing a finding costs them the finding.
/// </remarks>
public sealed partial class PostedFindingIndex(
    IThreadMemoryEmbedder embedder,
    IPostedFindingRepository repository,
    IOptions<AiReviewOptions> options,
    ILogger<PostedFindingIndex> logger) : IPostedFindingIndex
{
    /// <summary>Reported when the index could not answer, so an operator can tell a miss from a gap.</summary>
    internal const string DegradedComponent = "posted_finding_index";

    /// <summary>The shipped floor, used when the configured one is missing or not a usable number.</summary>
    private const float DefaultMinSimilarity = 0.85f;

    private const string DegradedCause =
        "Cross-increment duplicate protection ran without the posted-finding index.";

    // One unbound or failing embedding model would otherwise cost a call per finding for the rest of the
    // pass, each failing the same way. The same latch the thread-memory suppression path uses.
    //
    // Held separately for the two phases on purpose. They run at different times and cost different things:
    // a lookup that gives up loses one suppression, while indexing that gives up loses the whole increment's
    // rows and lets the next increment repeat everything. A failure while reading must not decide that.
    private readonly ConcurrentDictionary<Guid, byte> _lookupEmbeddingFailuresByClient = new();
    private readonly ConcurrentDictionary<Guid, byte> _indexingEmbeddingFailuresByClient = new();
    private readonly AiReviewOptions _opts = options.Value;

    /// <inheritdoc />
    public async Task<PostedFindingMatchDto> FindDuplicateAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string findingMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(findingMessage) ||
            string.IsNullOrWhiteSpace(repositoryId) ||
            pullRequestId <= 0)
        {
            return PostedFindingMatchDto.NoMatch();
        }

        var queryVector = await this.TryEmbedAsync(this._lookupEmbeddingFailuresByClient, clientId, findingMessage, ct);
        if (queryVector is null)
        {
            return PostedFindingMatchDto.NoMatch([DegradedComponent], DegradedCause);
        }

        try
        {
            // Asked without the acting threshold so the closest candidate comes back either way. One query
            // answers both questions: whether to withhold, and how close the nearest miss was, which is the
            // only evidence that would show the threshold set too high.
            var closest = await repository.FindClosestInPullRequestAsync(
                clientId,
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                queryVector,
                0f,
                ct);

            if (closest is null)
            {
                return PostedFindingMatchDto.NoMatch();
            }

            return closest.SimilarityScore >= this.EffectiveMinSimilarity
                ? PostedFindingMatchDto.Match(
                    closest.ProviderThreadId,
                    closest.PostedFindingId,
                    closest.SimilarityScore,
                    closest.AutoResolvedByProPr)
                : PostedFindingMatchDto.NearMiss(closest.ProviderThreadId, closest.SimilarityScore);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogLookupFailed(logger, pullRequestId, clientId, ex);
            return PostedFindingMatchDto.NoMatch([DegradedComponent], DegradedCause);
        }
    }

    /// <inheritdoc />
    public async Task RecordPostedFindingsAsync(
        IReadOnlyList<PostedFindingEntry> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var records = new List<PostedFindingRecord>(entries.Count);

            foreach (var entry in entries)
            {
                // The finding text alone. No file, no line, no severity: all three were observed drifting
                // between increments while the concern stayed the same, so none of them may enter the key.
                var vector = await this.TryEmbedAsync(
                    this._indexingEmbeddingFailuresByClient,
                    entry.ClientId,
                    entry.FindingMessage,
                    ct);
                if (vector is null)
                {
                    continue;
                }

                records.Add(
                    new PostedFindingRecord
                    {
                        Id = Guid.NewGuid(),
                        ClientId = entry.ClientId,
                        RepositoryId = entry.RepositoryId,
                        PullRequestId = entry.PullRequestId,
                        ProviderThreadId = entry.ProviderThreadId,
                        ReviewJobId = entry.ReviewJobId,
                        IterationId = entry.IterationId,
                        FilePath = entry.FilePath,
                        Severity = entry.Severity,
                        FindingMessage = entry.FindingMessage,
                        AutoResolvedByProPr = entry.AutoResolvedByProPr,
                        EmbeddingVector = vector,
                        CreatedAt = now,
                    });
            }

            if (records.Count == 0)
            {
                // Every finding failed to embed. Silence here would read as "nothing to index", when in fact
                // the whole increment is missing from the index and the next one will repeat all of it.
                LogNothingIndexed(logger, entries.Count);
                return;
            }

            await repository.AddMissingAsync(records, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogIndexingFailed(logger, entries.Count, ex);
        }
    }

    /// <summary>
    ///     The similarity floor this index matches on, clamped into range. The option carries a
    ///     <c>[Range]</c> attribute but nothing validates it at startup, and a floor that fell out of range
    ///     would either suppress everything or nothing.
    /// </summary>
    private float EffectiveMinSimilarity
    {
        get
        {
            var configured = this._opts.PostedFindingMinSimilarity;

            // A threshold nobody validates decides whether findings reach the pull request. Not-a-number
            // compares false against everything, which would make the tier quietly never match, and zero
            // would make it suppress against anything less than a right angle away. Both fall back to the
            // shipped default rather than silently becoming policy.
            return float.IsNaN(configured) || configured <= 0f
                ? DefaultMinSimilarity
                : Math.Min(configured, 1f);
        }
    }

    private async Task<float[]?> TryEmbedAsync(
        ConcurrentDictionary<Guid, byte> failureLatch,
        Guid clientId,
        string text,
        CancellationToken ct)
    {
        if (failureLatch.ContainsKey(clientId))
        {
            return null;
        }

        try
        {
            return await embedder.GenerateEmbeddingAsync(text, clientId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failureLatch.TryAdd(clientId, 0);
            LogEmbeddingFailed(logger, clientId, ex);
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Posted-finding index embedding failed for client {ClientId}; cross-increment duplicate "
                  + "protection is degraded for the rest of this pass")]
    private static partial void LogEmbeddingFailed(ILogger logger, Guid clientId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Posted-finding index lookup failed for pull request {PullRequestId} client {ClientId}")]
    private static partial void LogLookupFailed(
        ILogger logger,
        int pullRequestId,
        Guid clientId,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Indexing {FindingCount} posted findings failed; later increments may repeat them")]
    private static partial void LogIndexingFailed(ILogger logger, int findingCount, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "None of the {FindingCount} findings posted by this review could be embedded, so none were "
                  + "indexed and a later increment may repeat all of them")]
    private static partial void LogNothingIndexed(ILogger logger, int findingCount);
}
