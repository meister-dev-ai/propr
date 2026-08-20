// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Prepared local repository state leased to one review execution.
/// </summary>
/// <remarks>
///     Only the head revision is checked out. Everything the target side of a review needs is read from the
///     mirror's object store at <see cref="BaseSha" />, so there is no second workspace path.
/// </remarks>
public sealed record ReviewRepositoryWorkspaceLease(
    Guid JobId,
    string WorkspaceKey,
    string MirrorPath,
    string HeadWorkspacePath,
    string HeadSha,
    string BaseSha,
    string MergeBaseSha,
    DateTimeOffset PreparedAt,
    DateTimeOffset LastAccessedAt,
    string Status,
    ReviewWorkspaceFailure? Failure = null);
