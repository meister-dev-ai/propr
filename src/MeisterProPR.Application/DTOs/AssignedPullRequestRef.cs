namespace MeisterProPR.Application.DTOs;

/// <summary>Lightweight reference to an ADO pull request assigned for review.</summary>
/// <param name="OrganizationUrl">Base URL of the ADO organization.</param>
/// <param name="ProjectId">ADO project ID or name.</param>
/// <param name="RepositoryId">ADO repository ID.</param>
/// <param name="PullRequestId">Numeric pull request ID.</param>
/// <param name="LatestIterationId">ID of the latest PR iteration.</param>
public sealed record AssignedPullRequestRef(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int LatestIterationId);
