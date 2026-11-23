// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Selects which changed files still require Reviewing execution after completion and exclusion rules are applied.
/// </summary>
public static class ReviewFileSelectionService
{
    /// <summary>
    ///     Splits changed files into reviewable, excluded and unchanged moved sets after removing already-completed
    ///     file results. An exclusion pattern takes precedence, so a moved file that a pattern matches counts as
    ///     excluded.
    /// </summary>
    /// <param name="changedFiles">The changed files in scope for this review.</param>
    /// <param name="existingResults">Per-file results already persisted for this job, keyed by path.</param>
    /// <param name="exclusionRules">The repository's exclusion rules.</param>
    /// <param name="currentPassKeys">
    ///     The client's currently configured review passes. A completed result recorded under a different set
    ///     is reviewed again: it is finished work, but not the work the configuration now asks for.
    /// </param>
    public static ReviewFileSelectionResult SelectFilesForReview(
        IReadOnlyList<ChangedFile> changedFiles,
        IReadOnlyDictionary<string, ReviewFileResult> existingResults,
        ReviewExclusionRules exclusionRules,
        IReadOnlyList<string>? currentPassKeys = null)
    {
        var passKeys = currentPassKeys ?? [];
        var completedFiles = existingResults
            .Where(kvp => kvp.Value.IsComplete
                          && ReviewPassSignature.Matches(kvp.Value.ReviewedPassKeys, passKeys))
            .Select(kvp => kvp.Key)
            .ToHashSet();

        var filesToReview = new List<ChangedFile>();
        var excludedFiles = new List<ChangedFile>();
        var unchangedMoveFiles = new List<ChangedFile>();
        foreach (var file in changedFiles.Where(file => !completedFiles.Contains(file.Path)))
        {
            if (exclusionRules.HasPatterns && exclusionRules.Matches(file.Path))
            {
                excludedFiles.Add(file);
            }
            else if (IsUnchangedMove(file))
            {
                unchangedMoveFiles.Add(file);
            }
            else
            {
                filesToReview.Add(file);
            }
        }

        return new ReviewFileSelectionResult(filesToReview, excludedFiles, unchangedMoveFiles);
    }

    /// <summary>
    ///     Whether the pull request only moves or renames a file, without changing its content. Such a file is a
    ///     rename with a known original path whose source confirmed that the content is the same on both sides
    ///     (<see cref="ChangedFile.ContentUnchanged" />), and whose diff adds or removes no line. A diff without
    ///     changed lines alone is not enough, because a failed read of either side also produces one. A binary file
    ///     does not count, because its diff is empty whether its content changed or not.
    /// </summary>
    /// <param name="file">The changed file.</param>
    public static bool IsUnchangedMove(ChangedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return file.ChangeType == ChangeType.Rename
               && !string.IsNullOrWhiteSpace(file.OriginalPath)
               && !file.IsBinary
               && file.ContentUnchanged
               && !ReviewDiffProcessor.HasChangedLines(file.UnifiedDiff);
    }

    /// <summary>
    ///     The reason ProPR records on the file result of a file the pull request only moves or renames. The reason
    ///     appears in the job protocol beside the file.
    /// </summary>
    /// <param name="file">A file for which <see cref="IsUnchangedMove" /> holds.</param>
    public static string DescribeUnchangedMove(ChangedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return $"Moved from {file.OriginalPath} without content changes";
    }
}
