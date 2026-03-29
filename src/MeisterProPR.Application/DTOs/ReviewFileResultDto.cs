namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries a summary of a per-file review pass.
/// </summary>
/// <param name="Id">Unique identifier of the file result.</param>
/// <param name="FilePath">Path of the file reviewed.</param>
/// <param name="IsComplete">True if the pass completed successfully or was excluded.</param>
/// <param name="IsFailed">True if the pass failed permanently.</param>
/// <param name="IsExcluded">True if the file was excluded from AI review by a matching exclusion pattern.</param>
/// <param name="ExclusionReason">The glob pattern that matched this file, when <paramref name="IsExcluded"/> is true.</param>
/// <param name="PerFileSummary">AI-generated summary for this file pass.</param>
/// <param name="CommentCount">Number of comments produced for this file.</param>
public sealed record ReviewFileResultDto(
    Guid Id,
    string FilePath,
    bool IsComplete,
    bool IsFailed,
    bool IsExcluded,
    string? ExclusionReason,
    string? PerFileSummary,
    int CommentCount);
