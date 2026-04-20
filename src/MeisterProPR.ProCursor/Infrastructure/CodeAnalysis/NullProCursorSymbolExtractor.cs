// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.CodeAnalysis.ProCursor;

/// <summary>
///     Placeholder symbol extractor until the Roslyn-backed implementation ships.
/// </summary>
public sealed class NullProCursorSymbolExtractor : IProCursorSymbolExtractor
{
    /// <inheritdoc />
    public Task<ProCursorSymbolExtractionResult> ExtractAsync(
        ProCursorMaterializedSource materializedSource,
        Guid snapshotId,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            new ProCursorSymbolExtractionResult(
                [],
                [],
                false,
                "symbol_extraction_not_configured"));
    }
}
