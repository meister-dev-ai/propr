// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result of selecting the per-file execution set after completion and exclusion rules are applied.
/// </summary>
public sealed record ReviewFileSelectionResult(
    IReadOnlyList<ChangedFile> FilesToReview,
    IReadOnlyList<ChangedFile> ExcludedFiles);
