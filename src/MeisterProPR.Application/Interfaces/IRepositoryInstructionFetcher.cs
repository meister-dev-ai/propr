// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Fetches repository-specific AI review instructions from the target branch of a repository.
/// </summary>
public interface IRepositoryInstructionFetcher
{
    /// <summary>
    ///     Fetches all valid <see cref="RepositoryInstruction" /> objects from the repository's instruction directory.
    /// </summary>
    /// <param name="organizationUrl">Azure DevOps organisation URL.</param>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="repositoryId">Repository identifier.</param>
    /// <param name="targetBranch">The target branch from which to read instructions.</param>
    /// <param name="clientId">Optional client identifier used to resolve ADO credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RepositoryInstruction>> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken);
}
