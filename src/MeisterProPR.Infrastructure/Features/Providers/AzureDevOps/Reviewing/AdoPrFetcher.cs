// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

public sealed partial class AdoPrFetcher(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoPrFetcher> logger) : IPullRequestFetcher, IProviderPullRequestFetcher
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<PullRequestRef> FetchRefAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);

        var pr = await gitClient.GetPullRequestAsync(
            projectId,
            repositoryId,
            pullRequestId,
            cancellationToken: cancellationToken);

        var status = pr.Status switch
        {
            PullRequestStatus.Abandoned => PrStatus.Abandoned,
            PullRequestStatus.Completed => PrStatus.Completed,
            _ => PrStatus.Active,
        };

        return new PullRequestRef(pr.SourceRefName ?? "", pr.TargetRefName ?? "", status);
    }

    public async Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        ReviewRevision? compareToReviewRevision = null,
        IReviewRepositoryWorkspace? workspace = null)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken);
        var authorizedIdentityId = connection.AuthorizedIdentity?.Id;
        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);

        // Get PR metadata
        var pr = await gitClient.GetPullRequestAsync(
            projectId,
            repositoryId,
            pullRequestId,
            cancellationToken: cancellationToken);

        // Get iteration for source/base commit SHAs
        var iteration = await gitClient.GetPullRequestIterationAsync(
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            cancellationToken: cancellationToken);

        var sourceCommit = iteration.SourceRefCommit?.CommitId
                           ?? pr.LastMergeSourceCommit?.CommitId ?? "";

        var baseCommit = iteration.CommonRefCommit?.CommitId
                         ?? pr.LastMergeTargetCommit?.CommitId ?? "";

        // On a re-review pass, fetch only the delta (files changed since the last reviewed
        // iteration) with full content.  Also fetch the full cumulative manifest (no content)
        // so the AI still has the complete PR scope for context.
        var deltaChanges = await AdoPullRequestIterationChangePager.LoadAllAsync(
            (top, skip, ct) => gitClient.GetPullRequestIterationChangesAsync(
                projectId,
                repositoryId,
                pullRequestId,
                iterationId,
                top,
                skip,
                compareToIterationId,
                cancellationToken: ct),
            cancellationToken);

        var changedFiles = new List<ChangedFile>();
        foreach (var change in deltaChanges)
        {
            var changedFile = await this.CreateChangedFileFromChangeAsync(
                gitClient,
                projectId,
                repositoryId,
                sourceCommit,
                baseCommit,
                change,
                cancellationToken,
                workspace);

            if (changedFile != null)
            {
                changedFiles.Add(changedFile);
            }
        }

        // Build the full PR manifest (paths + change types only, no content download) when
        // we're doing a delta review.  On a first-pass fetch compareTo is null and the two
        // lists are identical, so we skip the extra API call.
        IReadOnlyList<ChangedFileSummary>? allChangedFileSummaries = null;
        if (compareToIterationId.HasValue)
        {
            var allChanges = await AdoPullRequestIterationChangePager.LoadAllAsync(
                (top, skip, ct) => gitClient.GetPullRequestIterationChangesAsync(
                    projectId,
                    repositoryId,
                    pullRequestId,
                    iterationId,
                    top,
                    skip,
                    cancellationToken: ct),
                cancellationToken);

            allChangedFileSummaries = allChanges
                .Select(CreateSummaryFromChange)
                .OfType<ChangedFileSummary>()
                .ToList()
                .AsReadOnly();
        }

        var existingThreads = await this.FetchExistingThreadsAsync(
            gitClient,
            projectId,
            repositoryId,
            pullRequestId,
            cancellationToken);

        return new PullRequest(
            organizationUrl,
            projectId,
            repositoryId,
            pr.Repository?.Name ?? repositoryId,
            pullRequestId,
            iterationId,
            pr.Title ?? "",
            pr.Description,
            pr.SourceRefName ?? "",
            pr.TargetRefName ?? "",
            changedFiles.AsReadOnly(),
            ExistingThreads: existingThreads,
            AllChangedFileSummaries: allChangedFileSummaries,
            AuthorizedIdentityId: authorizedIdentityId);
    }

    public async Task<ChangedFile?> FetchFileDiffAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        string filePath,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken);
        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);

        var pr = await gitClient.GetPullRequestAsync(
            projectId,
            repositoryId,
            pullRequestId,
            cancellationToken: cancellationToken);

        var iteration = await gitClient.GetPullRequestIterationAsync(
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            cancellationToken: cancellationToken);

        var sourceCommit = iteration.SourceRefCommit?.CommitId
                           ?? pr.LastMergeSourceCommit?.CommitId ?? "";

        var baseCommit = iteration.CommonRefCommit?.CommitId
                         ?? pr.LastMergeTargetCommit?.CommitId ?? "";

        // Read from the ADO item API with the leading slash it expects, but emit a repo-relative path.
        var apiPath = ToAdoApiPath(filePath);
        var repoRelativePath = ToRepoRelativePath(filePath);
        var isBinary = BinaryFileDetector.IsBinary(repoRelativePath);

        if (isBinary)
        {
            return new ChangedFile(repoRelativePath, ChangeType.Edit, string.Empty, string.Empty, true);
        }

        var headContent = sourceCommit.Length >= 6
            ? await this.TryResolveHeadContentAsync(gitClient, projectId, repositoryId, apiPath, sourceCommit, cancellationToken)
            : string.Empty;

        var baseContent = baseCommit.Length >= 6
            ? await this.TryResolveBaseContentAsync(gitClient, projectId, repositoryId, apiPath, baseCommit, cancellationToken)
            : string.Empty;

        if (string.IsNullOrEmpty(headContent) && string.IsNullOrEmpty(baseContent))
        {
            return null;
        }

        var fallbackChangeType = string.IsNullOrEmpty(headContent) ? ChangeType.Delete : ChangeType.Edit;
        var changeType = string.IsNullOrEmpty(baseContent) ? ChangeType.Add : fallbackChangeType;

        var diff = BuildUnifiedDiff(baseContent, headContent, repoRelativePath);

        return new ChangedFile(repoRelativePath, changeType, headContent, diff);
    }

    public async Task<IReadOnlyList<PrCommentThread>> FetchThreadsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        // Passive thread-retention path: fetch only the pull request's comment threads in one API call and
        // never download changed-file content. This runs once per crawl cycle when thread retention is on, so
        // it must not pull the whole pull request the way the review-grade FetchAsync does.
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            cancellationToken);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
        await connection.ConnectAsync(cancellationToken);
        var gitClient = await connection.GetClientAsync<GitHttpClient>(cancellationToken);

        return await this.FetchExistingThreadsAsync(gitClient, projectId, repositoryId, pullRequestId, cancellationToken);
    }

    // Azure DevOps returns repo-root-absolute item paths (with a leading slash); the rest of the pipeline
    // and the other provider adapters use repo-relative paths without it. Paths that leave this adapter as
    // ChangedFile/ChangedFileSummary are normalized to the repo-relative form so downstream path matching
    // (comment anchoring, changed-line scope, retention diff keys) is provider-neutral.
    internal static string ToRepoRelativePath(string path)
    {
        return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/').TrimStart('/');
    }

    // The inverse: the Azure DevOps item APIs expect the leading slash, so a repo-relative path handed
    // back to the ADO client is given it.
    private static string ToAdoApiPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    internal static ChangedFileSummary? CreateSummaryFromChange(GitPullRequestChange change)
    {
        if (change.Item?.IsFolder == true || string.IsNullOrEmpty(change.Item?.Path))
        {
            return null;
        }

        var changeType = change.ChangeType switch
        {
            VersionControlChangeType.Add => ChangeType.Add,
            VersionControlChangeType.Delete => ChangeType.Delete,
            _ when change.ChangeType.HasFlag(VersionControlChangeType.Rename) => ChangeType.Rename,
            _ => ChangeType.Edit,
        };

        return new ChangedFileSummary(ToRepoRelativePath(change.Item.Path), changeType);
    }

    /// <summary>
    ///     Creates a change file representation from a GitPullRequestChange, including fetching file contents and generating
    ///     diffs as needed.
    /// </summary>
    private async Task<ChangedFile?> CreateChangedFileFromChangeAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        string sourceCommit,
        string baseCommit,
        GitPullRequestChange change,
        CancellationToken cancellationToken,
        IReviewRepositoryWorkspace? workspace = null)
    {
        if (change.Item?.IsFolder == true)
        {
            return null;
        }

        var path = change.Item?.Path ?? "";
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var changeType = change.ChangeType switch
        {
            VersionControlChangeType.Add => ChangeType.Add,
            VersionControlChangeType.Edit => ChangeType.Edit,
            VersionControlChangeType.Delete => ChangeType.Delete,
            _ when change.ChangeType.HasFlag(VersionControlChangeType.Rename) => ChangeType.Rename,
            _ => ChangeType.Edit,
        };

        // For renames/moves, the original path is stored in SourceServerItem.
        // We must fetch base content at that path on the base commit; the new path didn't exist there.
        // SourceServerItem may be null when ADO does not populate it (e.g. cross-tree moves); skip the
        // base fetch in that case rather than using the new path which would produce a spurious 404.
        var originalPath = changeType == ChangeType.Rename
            ? change.SourceServerItem
            : null;

        var renameWithMissingSource = changeType == ChangeType.Rename && originalPath is null;

        var isBinary = BinaryFileDetector.IsBinary(path);

        var headContent = "";
        var baseContent = "";
        string? diff = null;

        if (!isBinary)
        {
            if (workspace is not null)
            {
                // Read content directly from the local git workspace — avoids N GetItemAsync calls
                if (changeType != ChangeType.Delete)
                {
                    headContent = await workspace.ReadFileAsync(path, "source", cancellationToken) ?? "";
                }

                if (changeType != ChangeType.Add && !renameWithMissingSource)
                {
                    var basePathToRead = originalPath ?? path;
                    baseContent = await workspace.ReadFileAsync(basePathToRead, "target", cancellationToken) ?? "";
                }
                else if (renameWithMissingSource)
                {
                    logger.LogInformation(
                        "Skipping base content fetch for renamed file {Path}: SourceServerItem is not available",
                        path);
                }

                diff = await workspace.GetUnifiedDiffAsync(path, cancellationToken);
            }
            else
            {
                if (changeType != ChangeType.Delete && sourceCommit.Length >= 6)
                {
                    headContent = await this.TryResolveHeadContentAsync(
                        gitClient,
                        projectId,
                        repositoryId,
                        path,
                        sourceCommit,
                        cancellationToken);
                }

                if (changeType != ChangeType.Add && !renameWithMissingSource && baseCommit.Length >= 6)
                {
                    var basePathToFetch = originalPath ?? path;
                    baseContent = await this.TryResolveBaseContentAsync(
                        gitClient,
                        projectId,
                        repositoryId,
                        basePathToFetch,
                        baseCommit,
                        cancellationToken);
                }
                else if (renameWithMissingSource)
                {
                    logger.LogInformation(
                        "Skipping base content fetch for renamed file {Path}: SourceServerItem is not available",
                        path);
                }
                else if (changeType != ChangeType.Add && baseCommit.Length < 6)
                {
                    logger.LogInformation(
                        "Skipping base content fetch for {Path}: base commit SHA is absent or malformed",
                        path);
                }
            }
        }

        var repoRelativePath = ToRepoRelativePath(path);
        var resolvedDiff = isBinary ? "" : diff ?? BuildUnifiedDiff(baseContent, headContent, repoRelativePath);

        return new ChangedFile(
            repoRelativePath,
            changeType,
            headContent,
            resolvedDiff,
            isBinary,
            originalPath is null ? null : ToRepoRelativePath(originalPath));
    }

    /// <summary>
    ///     Tries to fetch the base content of a change.
    /// </summary>
    private async Task<string> TryResolveBaseContentAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        string path,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        string baseContent;
        try
        {
            var item = await gitClient.GetItemAsync(
                projectId,
                repositoryId,
                path,
                null, // scopePath
                null, // recursionLevel
                null, // includeContentMetadata
                null, // latestProcessedChange
                null, // download
                new GitVersionDescriptor
                {
                    VersionType = GitVersionType.Commit,
                    Version = baseCommit,
                },
                true, // includeContent
                null, // resolveLfs
                null, // sanitize
                null, // userState
                cancellationToken);
            baseContent = item.Content ?? "";
        }
        catch (Exception ex)
        {
            LogBaseContentFetchWarning(logger, path, baseCommit, ex);
            baseContent = "";
        }

        return baseContent;
    }

    /// <summary>
    ///     Tries to fetch the head content of a change.
    /// </summary>
    private async Task<string> TryResolveHeadContentAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        string path,
        string sourceCommit,
        CancellationToken cancellationToken)
    {
        string headContent;
        try
        {
            var item = await gitClient.GetItemAsync(
                projectId,
                repositoryId,
                path,
                null, // scopePath
                null, // recursionLevel
                null, // includeContentMetadata
                null, // latestProcessedChange
                null, // download
                new GitVersionDescriptor
                {
                    VersionType = GitVersionType.Commit,
                    Version = sourceCommit,
                },
                true, // includeContent
                null, // resolveLfs
                null, // sanitize
                null, // userState
                cancellationToken);
            headContent = item.Content ?? "";
        }
        catch (Exception ex)
        {
            LogHeadContentFetchWarning(logger, path, sourceCommit, ex);
            headContent = "";
        }

        return headContent;
    }

    private static string BuildUnifiedDiff(string oldContent, string newContent, string filePath)
    {
        return UnifiedDiffBuilder.Build(oldContent, newContent, filePath);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to fetch head content for {Path} at commit {Commit}")]
    private static partial void LogHeadContentFetchWarning(ILogger logger, string path, string commit, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Failed to fetch base content for {Path} at commit {Commit}")]
    private static partial void LogBaseContentFetchWarning(ILogger logger, string path, string commit, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Failed to fetch existing comment threads for PR #{PullRequestId}. Proceeding without thread context.")]
    private static partial void LogThreadFetchWarning(ILogger logger, int pullRequestId, Exception ex);

    private async Task<IReadOnlyList<PrCommentThread>> FetchExistingThreadsAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawThreads = await gitClient.GetThreadsAsync(
                projectId,
                repositoryId,
                pullRequestId,
                cancellationToken: cancellationToken);

            return (rawThreads ?? [])
                .Where(t => !t.IsDeleted && t.Comments?.Count > 0)
                .Select(t => new PrCommentThread(
                    t.Id,
                    t.ThreadContext?.FilePath,
                    t.ThreadContext?.RightFileStart?.Line,
                    t.Comments!
                        .Where(c => !c.IsDeleted)
                        .Select(c => new PrThreadComment(
                            c.Author?.DisplayName ?? "Unknown",
                            c.Content ?? "",
                            Guid.TryParse(c.Author?.Id, out var aid) ? aid : null,
                            c.Id,
                            c.PublishedDate != default
                                ? new DateTimeOffset(c.PublishedDate, TimeSpan.Zero)
                                : null))
                        .ToList()
                        .AsReadOnly(),
                    t.Status.ToString()))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            LogThreadFetchWarning(logger, pullRequestId, ex);
            return [];
        }
    }
}
