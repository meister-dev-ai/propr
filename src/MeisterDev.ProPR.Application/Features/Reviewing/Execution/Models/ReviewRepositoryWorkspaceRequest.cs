// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Provider-neutral request used to prepare a local review repository workspace.
/// </summary>
/// <param name="MaxRepositoryMegabytes">
///     The client's repository-size bound in mebibytes (1,048,576 bytes), or null when the client set none.
///     Preparation watches how large the mirror and the checkout grow while git transfers them and stops the
///     transfer once the bound is passed, so a repository over the bound never occupies the disk of the host
///     that would have reviewed it.
/// </param>
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
    IReadOnlyList<ChangedPathSnapshot>? ChangedPathSnapshots = null,
    int? MaxRepositoryMegabytes = null);
