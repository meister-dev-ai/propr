namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a pull request and the files changed within it.
/// </summary>
/// <param name="OrganizationUrl">Organization URL containing the repository.</param>
/// <param name="ProjectId">Project identifier.</param>
/// <param name="RepositoryId">Repository identifier.</param>
/// <param name="PullRequestId">Pull request numeric id.</param>
/// <param name="IterationId">Iteration id within the pull request.</param>
/// <param name="Title">Title of the pull request.</param>
/// <param name="Description">Optional description of the pull request.</param>
/// <param name="SourceBranch">Source branch name.</param>
/// <param name="TargetBranch">Target branch name.</param>
/// <param name="ChangedFiles">List of changed files in the pull request.</param>
public sealed record PullRequest(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    string Title,
    string? Description,
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<ChangedFile> ChangedFiles);