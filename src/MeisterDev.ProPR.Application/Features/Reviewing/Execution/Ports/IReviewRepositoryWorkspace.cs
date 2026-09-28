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
    ///     Returns a unified diff between the prepared merge-base and head revisions for the requested path.
    /// </summary>
    Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct);

    /// <summary>
    ///     Returns the added and removed lines across <paramref name="paths" />, between the prepared merge-base
    ///     and head revisions. Reported as one number, because the bound it answers is on the size of the change
    ///     and not on its direction. A path git reports as binary contributes nothing.
    /// </summary>
    /// <param name="paths">
    ///     The paths to measure. Each distinct path counts once: a path repeated in the collection is measured
    ///     once, so a caller that passes duplicates does not have its review refused for a size it does not have.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<int> CountChangedLinesAsync(IReadOnlyCollection<string> paths, CancellationToken ct);

    /// <summary>
    ///     Returns the size in bytes of the unified diff of <paramref name="paths" />, between the prepared
    ///     merge-base and head revisions. The count is taken while the diff streams past, so a diff large
    ///     enough to breach the bound never occupies memory in the process asking for its size.
    /// </summary>
    /// <param name="paths">The paths to measure, each distinct path counting once.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<long> CountDiffBytesAsync(IReadOnlyCollection<string> paths, CancellationToken ct);
}
