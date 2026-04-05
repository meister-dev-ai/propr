// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Extracts text chunks from one materialized ProCursor source state for persistence and retrieval.
/// </summary>
public interface IProCursorChunkExtractor
{
    /// <summary>Extracts the searchable chunk set from the materialized source.</summary>
    Task<IReadOnlyList<ProCursorExtractedChunk>> ExtractAsync(
        ProCursorKnowledgeSource source,
        ProCursorMaterializedSource materializedSource,
        CancellationToken ct = default);
}
