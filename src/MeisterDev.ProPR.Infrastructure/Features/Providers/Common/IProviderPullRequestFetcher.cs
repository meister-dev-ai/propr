// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal interface IProviderPullRequestFetcher
{
    ScmProvider Provider { get; }

    Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        ReviewRevision? compareToReviewRevision = null,
        IReviewRepositoryWorkspace? workspace = null);

    /// <summary>
    ///     Fetches only the pull request's comment threads, without downloading changed-file content.
    ///     Default implementation performs a full fetch and extracts the threads. ADO overrides this with a
    ///     single thread-API call so the passive thread-retention observer never pulls whole pull-request
    ///     contents on each crawl cycle.
    /// </summary>
    async Task<IReadOnlyList<PrCommentThread>> FetchThreadsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var pr = await this.FetchAsync(organizationUrl, projectId, repositoryId, pullRequestId, 1, null, clientId, cancellationToken);
        return pr.ExistingThreads ?? [];
    }

    /// <summary>
    ///     Fetches the comments in the pull request's own conversation, which belong to no review thread.
    /// </summary>
    /// <remarks>
    ///     Kept apart from <see cref="FetchThreadsAsync" /> rather than folded into it, because the review
    ///     prompt, the file reviewer and the thread pass all read the thread set and none of them wants
    ///     comments that sit on no file. Only mention scanning asks for these: a question addressed to the
    ///     reviewer is as likely to be asked in the conversation as on a line of code.
    ///     The default is empty, which is right for a provider whose thread set already holds these comments:
    ///     Azure DevOps models every pull request comment as a thread, so it needs no separate read. Every
    ///     other adapter overrides this, GitLab included — it returns standalone notes from the same
    ///     discussions endpoint as the rest, but its thread read drops them for being anchored to no file.
    /// </remarks>
    Task<IReadOnlyList<PrCommentThread>> FetchConversationThreadsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<PrCommentThread>>([]);
    }

    /// <summary>
    ///     Fetches pull-request metadata and comment threads with no changed-file content, for the thread
    ///     pass. Default implementation performs a full fetch, which every adapter overrides; the fallback
    ///     exists so a new adapter is correct before it is cheap.
    /// </summary>
    async Task<PullRequest> FetchThreadContextAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        bool includeChangedFileManifest = false)
    {
        // A full fetch already includes every changed file, so a manifest the caller requested is derived
        // from them at no additional cost in this fallback.
        return await this.FetchAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            null,
            clientId,
            cancellationToken);
    }

    /// <summary>
    ///     Default implementation: performs a full fetch and extracts just the ref info.
    ///     ADO overrides this with a lightweight single-API-call implementation.
    /// </summary>
    async Task<PullRequestRef> FetchRefAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var pr = await this.FetchAsync(organizationUrl, projectId, repositoryId, pullRequestId, 1, null, clientId, cancellationToken);
        return new PullRequestRef(pr.SourceBranch, pr.TargetBranch, pr.Status);
    }

    /// <summary>
    ///     Default implementation: performs a full fetch and filters to the requested file.
    ///     ADO overrides this with a targeted single-file implementation that avoids
    ///     downloading content for every changed file.
    /// </summary>
    async Task<ChangedFile?> FetchFileDiffAsync(
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
        var pr = await this.FetchAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            compareToIterationId,
            clientId,
            cancellationToken);

        return pr.ChangedFiles.FirstOrDefault(file =>
            string.Equals(file.Path, filePath, StringComparison.Ordinal)
            || string.Equals(file.OriginalPath, filePath, StringComparison.Ordinal));
    }
}
