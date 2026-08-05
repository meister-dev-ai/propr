// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Transfer object returned by a similarity query against the thread memory store.
/// </summary>
/// <param name="MemoryRecordId">Source <see cref="MeisterDev.ProPR.Domain.Entities.ThreadMemoryRecord" /> identifier.</param>
/// <param name="ThreadId">Source provider thread identifier.</param>
/// <param name="FilePath">File path from the historical thread. Null for PR-level threads.</param>
/// <param name="ResolutionSummary">AI-generated resolution summary from the stored record.</param>
/// <param name="SimilarityScore">Cosine similarity score (0–1) between the query vector and the stored vector.</param>
/// <param name="MatchSource">How the record was selected, for example <c>semantic</c> or <c>exact_file_fallback</c>.</param>
/// <param name="Source">How the memory was created, a resolved thread or an admin dismissal.</param>
/// <param name="Intent">
///     What the reviewer's resolution meant, a deliberate acceptance or a claimed fix. Null where the record
///     carries no reviewer resolution, and for records written before the outcome was recorded.
/// </param>
/// <param name="Clarity">How clearly the thread read as resolved, which qualifies the intent.</param>
public sealed record ThreadMemoryMatchDto(
    Guid MemoryRecordId,
    string ThreadId,
    string? FilePath,
    string ResolutionSummary,
    float SimilarityScore,
    string MatchSource = "semantic",
    MemorySource Source = MemorySource.ThreadResolved,
    ThreadResolutionIntent? Intent = null,
    ResolutionClarity? Clarity = null);
