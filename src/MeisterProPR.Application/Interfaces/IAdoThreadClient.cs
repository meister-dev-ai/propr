// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Manages pull request comment threads as a first-class reviewer participant.
///     Unlike <see cref="IAdoCommentPoster" /> (which reacts to mentions), this interface
///     enables proactive reviewer actions such as updating thread resolution status.
/// </summary>
public interface IAdoThreadClient
{
    /// <summary>
    ///     Updates the status of a pull request comment thread.
    /// </summary>
    /// <param name="organizationUrl">ADO organisation URL (e.g. <c>https://dev.azure.com/myorg</c>).</param>
    /// <param name="projectId">ADO project identifier or name.</param>
    /// <param name="repositoryId">ADO repository identifier (GUID string).</param>
    /// <param name="pullRequestId">Pull request number.</param>
    /// <param name="threadId">ADO thread identifier to update.</param>
    /// <param name="status">
    ///     Thread status string — must be a value accepted by the ADO API
    ///     (e.g. <c>"fixed"</c>, <c>"closed"</c>, <c>"active"</c>).
    ///     Case-insensitive.
    /// </param>
    /// <param name="clientId">Optional client identifier for per-client credential retrieval.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task UpdateThreadStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string status,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);
}
