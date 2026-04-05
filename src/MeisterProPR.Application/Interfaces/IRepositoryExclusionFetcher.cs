// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Fetches file exclusion patterns from <c>.meister-propr/exclude</c> on the target branch
///     of a repository. Returns built-in default patterns when the file is absent.
///     Files from the source branch are never read, preventing prompt injection via
///     attacker-controlled branches.
/// </summary>
public interface IRepositoryExclusionFetcher
{
    /// <summary>
    ///     Fetches exclusion patterns for the given repository and target branch.
    ///     Returns <see cref="MeisterProPR.Application.ValueObjects.ReviewExclusionRules.Default" />
    ///     when the <c>.meister-propr/exclude</c> file is absent or unreadable — never returns
    ///     <see langword="null" /> and never throws.
    /// </summary>
    /// <param name="organizationUrl">Azure DevOps organisation URL.</param>
    /// <param name="projectId">Project identifier.</param>
    /// <param name="repositoryId">Repository identifier.</param>
    /// <param name="targetBranch">The target branch from which to read the exclusion file.</param>
    /// <param name="clientId">Optional client identifier used to resolve ADO credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MeisterProPR.Application.ValueObjects.ReviewExclusionRules> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken);
}
