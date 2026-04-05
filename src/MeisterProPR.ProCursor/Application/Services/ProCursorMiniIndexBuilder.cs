// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Builds ephemeral review-target ProCursor symbol overlays for one PR context.
/// </summary>
public sealed class ProCursorMiniIndexBuilder(
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    IProCursorIndexSnapshotRepository snapshotRepository,
    IEnumerable<IProCursorMaterializer> materializers,
    IProCursorSymbolExtractor symbolExtractor,
    IOptions<ProCursorOptions> options,
    ILogger<ProCursorMiniIndexBuilder> logger)
{
    private readonly ProCursorOptions _options = options.Value;

    /// <summary>
    ///     Builds an ephemeral symbol overlay for the review-target branch described by the request.
    /// </summary>
    public async Task<ProCursorMiniIndexOverlay?> BuildAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.StateMode, "reviewTarget", StringComparison.OrdinalIgnoreCase) ||
            request.ReviewContext is null)
        {
            return null;
        }

        var source = await this.ResolveSourceAsync(request, ct);
        if (source is null)
        {
            logger.LogInformation(
                "ProCursor mini-index overlay unavailable for client {ClientId}: no matching repository source found for {RepositoryId}.",
                request.ClientId,
                request.ReviewContext.RepositoryId);
            return null;
        }

        var materializer = materializers.FirstOrDefault(candidate => candidate.SourceKind == source.SourceKind);
        if (materializer is null)
        {
            logger.LogWarning(
                "ProCursor mini-index overlay unavailable for source {SourceId}: no materializer is registered for {SourceKind}.",
                source.Id,
                source.SourceKind);
            return null;
        }

        var baseSnapshot = await snapshotRepository.GetLatestReadyAsync(source.Id, null, ct);
        var overlayBranch = new ProCursorTrackedBranch(
            Guid.NewGuid(),
            source.Id,
            request.ReviewContext.SourceBranch,
            ProCursorRefreshTriggerMode.Manual,
            true);
        string? materializedRootDirectory = null;

        try
        {
            var materializedSource = await materializer.MaterializeAsync(source, overlayBranch, null, ct);
            materializedRootDirectory = materializedSource.RootDirectory;

            var overlayId = Guid.NewGuid();
            var symbolExtraction = ShouldExtractSymbols(source)
                ? await symbolExtractor.ExtractAsync(materializedSource, overlayId, ct)
                : new ProCursorSymbolExtractionResult([], [], false, "text_only");
            var builtAt = DateTimeOffset.UtcNow;

            return new ProCursorMiniIndexOverlay(
                overlayId,
                baseSnapshot?.Id,
                symbolExtraction.SupportsSymbolQueries,
                symbolExtraction.Symbols,
                symbolExtraction.Edges,
                "fresh",
                builtAt,
                builtAt.AddMinutes(Math.Max(1, this._options.MiniIndexTtlMinutes)));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(materializedRootDirectory) && Directory.Exists(materializedRootDirectory))
            {
                Directory.Delete(materializedRootDirectory, true);
            }
        }
    }

    private async Task<ProCursorKnowledgeSource?> ResolveSourceAsync(ProCursorSymbolQueryRequest request, CancellationToken ct)
    {
        var sources = await knowledgeSourceRepository.ListByClientAsync(request.ClientId, ct);
        return sources
            .Where(source => source.IsEnabled)
            .Where(source => source.SourceKind == ProCursorSourceKind.Repository)
            .Where(source => !request.SourceId.HasValue || source.Id == request.SourceId.Value)
            .FirstOrDefault(source =>
                string.Equals(source.RepositoryId, request.ReviewContext!.RepositoryId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldExtractSymbols(ProCursorKnowledgeSource source)
    {
        return source.SourceKind == ProCursorSourceKind.Repository &&
               !string.Equals(source.SymbolMode, "text_only", StringComparison.OrdinalIgnoreCase);
    }
}
