// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Reads the latest revision of the pull request a thread pass belongs to and compares it with the revision the
///     pass was queued at.
/// </summary>
/// <remarks>
///     Each provider family has its own reader, because the providers identify a revision in different ways. Azure
///     DevOps numbers the iterations of a pull request, while GitHub, GitLab and Forgejo identify a revision by its
///     head commit.
/// </remarks>
public interface ILatestRevisionReader
{
    /// <summary>The provider family implemented by this reader.</summary>
    ScmProvider Provider { get; }

    /// <summary>
    ///     Reports whether the pull request has moved to a later revision than the one <paramref name="job" /> was
    ///     queued at.
    /// </summary>
    /// <param name="job">The thread pass whose queued revision is compared with the latest one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     <see langword="true" /> when the pull request has a later revision; <see langword="false" /> when the
    ///     queued revision is still the latest one or the provider reports no revision.
    /// </returns>
    Task<bool> HasMovedPastAsync(ThreadPassJob job, CancellationToken ct = default);
}
