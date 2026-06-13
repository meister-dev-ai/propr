// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries the unified diff for a single file that was reviewed in a review job.
///     The diff is re-fetched on demand from the source control provider.
/// </summary>
/// <param name="FilePath">Repository-relative path of the file.</param>
/// <param name="UnifiedDiff">Unified diff content (empty when the file is binary or unavailable).</param>
/// <param name="ChangeType">Type of change: Added, Modified, Deleted, Renamed, Copied, or Unknown.</param>
/// <param name="IsBinary">True when the file is binary and the diff cannot be rendered.</param>
/// <param name="OriginalPath">Previous path if the file was renamed; null otherwise.</param>
/// <param name="Availability">Indicates whether the diff is renderable and why.</param>
/// <param name="AvailabilityMessage">Human-readable explanation when the diff is not available.</param>
public sealed record FileDiffDto(
    string FilePath,
    string UnifiedDiff,
    string ChangeType,
    bool IsBinary,
    string? OriginalPath,
    string Availability,
    string? AvailabilityMessage);
