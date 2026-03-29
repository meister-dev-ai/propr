using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a pull request and the files changed within it.
/// </summary>
/// <param name="OrganizationUrl">Organization URL containing the repository.</param>
/// <param name="ProjectId">Project identifier.</param>
/// <param name="RepositoryId">Repository identifier.</param>
/// <param name="RepositoryName">Repository display name from ADO.</param>
/// <param name="PullRequestId">Pull request numeric id.</param>
/// <param name="IterationId">Iteration id within the pull request.</param>
/// <param name="Title">Title of the pull request.</param>
/// <param name="Description">Optional description of the pull request.</param>
/// <param name="SourceBranch">Source branch name.</param>
/// <param name="TargetBranch">Target branch name.</param>
/// <param name="ChangedFiles">
///     Files to review in this pass. On a first-pass review this is all files changed in the PR.
///     On a re-review it is the delta — only files that changed since the last reviewed iteration.
/// </param>
/// <param name="Status">Current status of the pull request (defaults to <see cref="PrStatus.Active" />).</param>
/// <param name="ExistingThreads">
///     Existing comment threads on the PR fetched before the review runs.
///     Used to provide AI context and to avoid posting duplicate bot comments.
///     Defaults to <c>null</c> (treated as empty — no deduplication).
/// </param>
/// <param name="AllChangedFileSummaries">
///     Full manifest of every file changed in the PR since the target branch (path + change type only).
///     Populated on re-review passes so the AI manifest section still covers the complete PR scope
///     even though <see cref="ChangedFiles" /> holds only the delta.
///     When <c>null</c> (first-pass fetch), <see cref="AllPrFileSummaries" /> derives it from
///     <see cref="ChangedFiles" />.
/// </param>
public sealed record PullRequest(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    string RepositoryName,
    int PullRequestId,
    int IterationId,
    string Title,
    string? Description,
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<ChangedFile> ChangedFiles,
    PrStatus Status = PrStatus.Active,
    IReadOnlyList<PrCommentThread>? ExistingThreads = null,
    IReadOnlyList<ChangedFileSummary>? AllChangedFileSummaries = null)
{
    /// <summary>
    ///     Full manifest of all files changed in the PR since the target branch (path + change type only).
    ///     On re-review passes this covers the entire PR scope; on first-pass it mirrors
    ///     <see cref="ChangedFiles" />.
    /// </summary>
    public IReadOnlyList<ChangedFileSummary> AllPrFileSummaries =>
        this.AllChangedFileSummaries ??
        this.ChangedFiles.Select(f => new ChangedFileSummary(f.Path, f.ChangeType)).ToList().AsReadOnly();
}
