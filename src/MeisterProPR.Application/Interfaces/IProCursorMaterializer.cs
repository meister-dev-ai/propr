// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Materializes one configured ProCursor knowledge source into an ephemeral working state for indexing.
/// </summary>
public interface IProCursorMaterializer
{
    /// <summary>The source kind handled by this materializer.</summary>
    ProCursorSourceKind SourceKind { get; }

    /// <summary>Materializes the requested source state into a temporary working directory.</summary>
    Task<ProCursorMaterializedSource> MaterializeAsync(
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch trackedBranch,
        string? requestedCommitSha,
        CancellationToken ct = default);
}
