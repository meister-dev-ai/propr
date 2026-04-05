// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs;

/// <summary>Lightweight reference to an ADO pull request assigned for review.</summary>
/// <param name="OrganizationUrl">Base URL of the ADO organization.</param>
/// <param name="ProjectId">ADO project ID or name.</param>
/// <param name="RepositoryId">ADO repository ID.</param>
/// <param name="PullRequestId">Numeric pull request ID.</param>
/// <param name="LatestIterationId">ID of the latest PR iteration.</param>
/// <param name="PrTitle">PR display title from ADO (optional — may be null when unavailable).</param>
/// <param name="RepositoryName">Repository display name from ADO (optional).</param>
/// <param name="SourceBranch">Source branch display name (optional, refs/heads/ stripped).</param>
/// <param name="TargetBranch">Target branch display name (optional, refs/heads/ stripped).</param>
public sealed record AssignedPullRequestRef(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int LatestIterationId,
    string? PrTitle = null,
    string? RepositoryName = null,
    string? SourceBranch = null,
    string? TargetBranch = null);
