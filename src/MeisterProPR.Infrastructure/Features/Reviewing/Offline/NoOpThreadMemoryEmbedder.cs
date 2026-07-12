// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Minimal embedder used by offline thread-memory reconsideration tests.
/// </summary>
public sealed class NoOpThreadMemoryEmbedder : IThreadMemoryEmbedder
{
    public Task<float[]> GenerateEmbeddingAsync(string compositeText, Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(new[] { 1.0f });
    }

    public Task<ThreadResolutionSummary> GenerateResolutionSummaryAsync(
        string? filePath,
        string? changeExcerpt,
        string commentHistory,
        Guid clientId,
        CancellationToken ct = default)
    {
        // Offline mode has no model to judge clarity; treat every resolved thread as storable so
        // fixture-based reconsideration behaves as it did before the store-time clarity gate.
        return Task.FromResult(new ThreadResolutionSummary(commentHistory, ResolutionClarity.ResolvedByChange));
    }
}
