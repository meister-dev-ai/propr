// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Extracts deterministic symbol information from a materialized ProCursor source state.
/// </summary>
public interface IProCursorSymbolExtractor
{
    /// <summary>Extracts symbol definitions and relationships for the given materialized source state.</summary>
    Task<ProCursorSymbolExtractionResult> ExtractAsync(
        ProCursorMaterializedSource materializedSource,
    Guid snapshotId,
        CancellationToken ct = default);
}
