// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Prepares and leases local git-backed repository workspaces for review execution.
/// </summary>
public interface IReviewRepositoryWorkspaceManager
{
    /// <summary>
    ///     Prepares a local workspace for the supplied review request, or returns a structured failure.
    /// </summary>
    Task<ReviewRepositoryWorkspacePreparationResult> PrepareAsync(
        ReviewRepositoryWorkspaceRequest request,
        CancellationToken ct);
}
