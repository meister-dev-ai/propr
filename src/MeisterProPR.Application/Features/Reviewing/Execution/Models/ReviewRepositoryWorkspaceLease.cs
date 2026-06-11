// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Prepared local repository state leased to one review execution.
/// </summary>
public sealed record ReviewRepositoryWorkspaceLease(
    Guid JobId,
    string WorkspaceKey,
    string MirrorPath,
    string HeadWorkspacePath,
    string BaseWorkspacePath,
    string HeadSha,
    string BaseSha,
    string MergeBaseSha,
    DateTimeOffset PreparedAt,
    DateTimeOffset LastAccessedAt,
    string Status,
    ReviewWorkspaceFailure? Failure = null);
