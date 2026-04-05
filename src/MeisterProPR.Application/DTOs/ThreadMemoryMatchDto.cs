// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Transfer object returned by a similarity query against the thread memory store.
/// </summary>
/// <param name="MemoryRecordId">Source <see cref="MeisterProPR.Domain.Entities.ThreadMemoryRecord" /> identifier.</param>
/// <param name="ThreadId">Source ADO thread identifier.</param>
/// <param name="FilePath">File path from the historical thread. Null for PR-level threads.</param>
/// <param name="ResolutionSummary">AI-generated resolution summary from the stored record.</param>
/// <param name="SimilarityScore">Cosine similarity score (0–1) between the query vector and the stored vector.</param>
/// <param name="MatchSource">How the record was selected, for example <c>semantic</c> or <c>exact_file_fallback</c>.</param>
/// <param name="Source">How the memory was created — resolved thread or admin dismissal.</param>
public sealed record ThreadMemoryMatchDto(
    Guid MemoryRecordId,
    int ThreadId,
    string? FilePath,
    string ResolutionSummary,
    float SimilarityScore,
    string MatchSource = "semantic",
    MemorySource Source = MemorySource.ThreadResolved);
