// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result of selecting the per-file execution set after completion and exclusion rules are applied.
/// </summary>
/// <param name="FilesToReview">The files that still need a review.</param>
/// <param name="ExcludedFiles">The files an exclusion pattern of the repository matches.</param>
/// <param name="UnchangedMoveFiles">
///     The files the pull request only moves or renames, without a changed line. They have nothing to review.
/// </param>
public sealed record ReviewFileSelectionResult(
    IReadOnlyList<ChangedFile> FilesToReview,
    IReadOnlyList<ChangedFile> ExcludedFiles,
    IReadOnlyList<ChangedFile> UnchangedMoveFiles);
