// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;

/// <summary>
///     One client's answer for a pull request, carrying the coordinates every review-scoped endpoint is
///     addressed by.
/// </summary>
/// <param name="ClientId">The client that covers the repository.</param>
/// <param name="Provider">The provider family the covering configuration belongs to.</param>
/// <param name="ProviderScopePath">
///     Scope path as configured, ready to pass to a review-scoped endpoint unchanged. This is a stored
///     value rather than a derived one, which is why it is returned instead of reconstructed by the caller.
/// </param>
/// <param name="ProviderProjectKey">Project, workspace, or namespace key as configured.</param>
/// <param name="RepositoryId">
///     Provider repository identity, or <see langword="null" /> when the covering configuration crawls
///     every repository in its scope and therefore names none. A caller holding <see langword="null" />
///     knows the repository is covered and cannot yet address it.
/// </param>
/// <param name="RepositoryName">Repository name recorded alongside the identity, when one is known.</param>
/// <param name="PullRequestId">The pull request number the query asked about, echoed for convenience.</param>
/// <param name="IsActiveConfiguration">
///     Whether the covering configuration is currently crawling. Resolution deliberately includes inactive
///     configurations, because past reviews of the repository remain readable.
/// </param>
public sealed record ResolvedPullRequestDto(
    Guid ClientId,
    ScmProvider Provider,
    string ProviderScopePath,
    string ProviderProjectKey,
    string? RepositoryId,
    string? RepositoryName,
    int PullRequestId,
    bool IsActiveConfiguration);

/// <summary>The full answer for one pull request address.</summary>
/// <param name="Matches">
///     Every client that covers the repository, most precisely matched first. Empty is a normal answer and
///     means no client covers it. More than one means the caller must choose.
/// </param>
public sealed record ResolvePullRequestResultDto(IReadOnlyList<ResolvedPullRequestDto> Matches);
