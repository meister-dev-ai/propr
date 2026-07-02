// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

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
