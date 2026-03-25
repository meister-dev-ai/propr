using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     Carries per-file review framing metadata for the AI review core.
///     When <see cref="ReviewSystemContext.PerFileHint" /> is non-null,
///     <c>ToolAwareAiReviewCore</c> uses <c>BuildPerFileSystemPrompt</c> and
///     <c>BuildPerFileUserMessage</c> instead of the whole-PR prompt builders.
/// </summary>
/// <param name="FilePath">The path of the file currently under review.</param>
/// <param name="FileIndex">1-based index of this file in the overall change manifest.</param>
/// <param name="TotalFiles">Total number of changed files in the PR.</param>
/// <param name="AllChangedFiles">The complete list of changed files (used to build the manifest section).</param>
public sealed record PerFileReviewHint(
    string FilePath,
    int FileIndex,
    int TotalFiles,
    IReadOnlyList<ChangedFile> AllChangedFiles);
