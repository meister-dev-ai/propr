// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

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
}
