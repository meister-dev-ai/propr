// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Local repository read contract used by workspace-backed review context tools.
/// </summary>
public interface IReviewRepositoryWorkspace : IAsyncDisposable
{
    /// <summary>
    ///     Gets the prepared lease backing this workspace instance.
    /// </summary>
    ReviewRepositoryWorkspaceLease Lease { get; }

    /// <summary>
    ///     Returns the locally derived changed files for the prepared revision pair.
    /// </summary>
    Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct);

    /// <summary>
    ///     Returns repository-relative file paths for the requested branch side.
    /// </summary>
    Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct);

    /// <summary>
    ///     Reads one repository-relative file from the requested branch side.
    /// </summary>
    Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct);

    /// <summary>
    ///     Returns a unified diff between the prepared merge-base and head revisions for the requested path. For a
    ///     renamed file, <paramref name="originalPath" /> is the path of the file in the merge-base revision, and the
    ///     diff pairs the two paths, so it contains the lines the pull request changed and not the whole file as
    ///     added.
    /// </summary>
    /// <param name="path">The path of the file in the head revision.</param>
    /// <param name="originalPath">The path of a renamed file in the merge-base revision, or <see langword="null" />.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<string?> GetUnifiedDiffAsync(string path, string? originalPath, CancellationToken ct);

    /// <summary>
    ///     Returns whether the file at <paramref name="path" /> in the prepared head revision is the same blob as the
    ///     file at <paramref name="originalPath" /> in the prepared merge-base revision, which means that a rename
    ///     from one path to the other changed no content. The answer comes from the object ids in the object store,
    ///     not from file contents. Returns <see langword="false" /> when either path does not resolve to a blob, and
    ///     when the lookup fails.
    /// </summary>
    /// <param name="originalPath">The path of the file in the merge-base revision.</param>
    /// <param name="path">The path of the file in the head revision.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<bool> IsUnchangedMoveAsync(string originalPath, string path, CancellationToken ct);

    /// <summary>
    ///     Returns the added and removed lines across <paramref name="paths" />, between the prepared merge-base
    ///     and head revisions. Reported as one number, because the bound it answers is on the size of the change
    ///     and not on its direction. A path git reports as binary contributes nothing.
    /// </summary>
    /// <param name="paths">
    ///     The paths to measure. Each distinct path counts once: a path repeated in the collection is measured
    ///     once, so a caller that passes duplicates does not have its review refused for a size it does not have.
    /// </param>
    /// <param name="originalPaths">
    ///     The merge-base paths of the renamed files among <paramref name="paths" />. Git pairs each renamed file with
    ///     its original path, as the review diff does, so a renamed file counts the lines it changed and a file that
    ///     was only moved counts none. An original path that git does not pair with a renamed file is not measured.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<int> CountChangedLinesAsync(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> originalPaths, CancellationToken ct);

    /// <summary>
    ///     Returns the size in bytes of the unified diff of <paramref name="paths" />, between the prepared
    ///     merge-base and head revisions. The count is taken while the diff streams past, so a diff large
    ///     enough to breach the bound never occupies memory in the process asking for its size.
    /// </summary>
    /// <param name="paths">The paths to measure, each distinct path counting once.</param>
    /// <param name="originalPaths">
    ///     The merge-base paths of the renamed files among <paramref name="paths" />, paired with them as in
    ///     <see cref="CountChangedLinesAsync" />.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<long> CountDiffBytesAsync(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> originalPaths, CancellationToken ct);

    /// <summary>
    ///     Returns the repository-relative paths of the files whose content differs between
    ///     <paramref name="revision" /> and the prepared head revision, compared without regard to case. Returns
    ///     <see langword="null" /> when the mirror cannot compare the two revisions, for example because it does not
    ///     hold <paramref name="revision" />.
    /// </summary>
    /// <param name="revision">The commit to compare the prepared head revision with.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlySet<string>?> GetPathsChangedSinceAsync(string revision, CancellationToken ct);
}
