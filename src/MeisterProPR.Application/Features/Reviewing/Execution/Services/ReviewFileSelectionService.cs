// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Selects which changed files still require Reviewing execution after completion and exclusion rules are applied.
/// </summary>
public static class ReviewFileSelectionService
{
    /// <summary>
    ///     Splits changed files into reviewable and excluded sets after removing already-completed file results.
    /// </summary>
    public static ReviewFileSelectionResult SelectFilesForReview(
        IReadOnlyList<ChangedFile> changedFiles,
        IReadOnlyDictionary<string, ReviewFileResult> existingResults,
        ReviewExclusionRules exclusionRules)
    {
        var completedFiles = existingResults
            .Where(kvp => kvp.Value.IsComplete)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        var remainingFiles = changedFiles.Where(file => !completedFiles.Contains(file.Path)).ToList();
        if (!exclusionRules.HasPatterns)
        {
            return new ReviewFileSelectionResult(remainingFiles, []);
        }

        var excludedFiles = remainingFiles.Where(file => exclusionRules.Matches(file.Path)).ToList();
        if (excludedFiles.Count == 0)
        {
            return new ReviewFileSelectionResult(remainingFiles, []);
        }

        var filesToReview = remainingFiles.Except(excludedFiles).ToList();
        return new ReviewFileSelectionResult(filesToReview, excludedFiles);
    }
}
