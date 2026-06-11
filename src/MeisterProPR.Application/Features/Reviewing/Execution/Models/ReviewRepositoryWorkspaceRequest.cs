// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Provider-neutral request used to prepare a local review repository workspace.
/// </summary>
public sealed record ReviewRepositoryWorkspaceRequest(
    Guid JobId,
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    RepositoryRef Repository,
    int PullRequestNumber,
    ReviewRevision ReviewRevision,
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<ChangedPathSnapshot>? ChangedPathSnapshots = null);
