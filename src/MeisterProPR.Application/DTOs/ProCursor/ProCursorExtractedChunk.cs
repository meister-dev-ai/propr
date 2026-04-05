// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     One extracted text chunk prepared for ProCursor embedding generation and snapshot persistence.
/// </summary>
public sealed record ProCursorExtractedChunk(
    string SourcePath,
    string ChunkKind,
    string? Title,
    int ChunkOrdinal,
    int? LineStart,
    int? LineEnd,
    string ContentHash,
    string ContentText);
