// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result of attempting to prepare a local review repository workspace.
/// </summary>
public sealed record ReviewRepositoryWorkspacePreparationResult(
    IReviewRepositoryWorkspace? Workspace,
    ReviewWorkspaceFailure? Failure)
{
    /// <summary>
    ///     Gets a value indicating whether a usable workspace was prepared.
    /// </summary>
    public bool Succeeded => this.Workspace is not null;
}
