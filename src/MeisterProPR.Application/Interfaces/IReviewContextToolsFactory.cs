// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Creates <see cref="IReviewContextTools" /> instances scoped to a single pull request review.
/// </summary>
public interface IReviewContextToolsFactory
{
    /// <summary>
    ///     Creates a new <see cref="IReviewContextTools" /> instance for the specified pull request.
    /// </summary>
    /// <param name="organizationUrl">Azure DevOps organization URL.</param>
    /// <param name="projectId">Azure DevOps project identifier.</param>
    /// <param name="repositoryId">Repository identifier.</param>
    /// <param name="sourceBranch">PR source branch; used as the enforced default for all file-fetch operations.</param>
    /// <param name="pullRequestId">Pull request numeric identifier.</param>
    /// <param name="iterationId">Pull request iteration identifier.</param>
    /// <param name="clientId">Optional client identifier for credential lookup.</param>
    /// <param name="knowledgeSourceIds">Optional persisted ProCursor source scope captured for the queued review job.</param>
    IReviewContextTools Create(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string sourceBranch,
        int pullRequestId,
        int iterationId,
        Guid? clientId,
        IReadOnlyList<Guid>? knowledgeSourceIds = null);
}
