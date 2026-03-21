namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Lightweight reference to an active pull request, used during mention scanning.
/// </summary>
/// <param name="OrganizationUrl">ADO organization URL.</param>
/// <param name="ProjectId">ADO project identifier.</param>
/// <param name="RepositoryId">ADO repository identifier.</param>
/// <param name="PullRequestId">ADO pull request number.</param>
/// <param name="LastUpdatedAt">When the pull request was last updated in ADO.</param>
public sealed record ActivePullRequestRef(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    DateTimeOffset LastUpdatedAt);
