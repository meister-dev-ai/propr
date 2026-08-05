// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.Budgeting.Models;

/// <summary>
///     The unit of work a spend baseline is being read for: which client and pull-request increment it belongs
///     to, and which row is its own so that row is left out of the total.
/// </summary>
/// <remarks>
///     <para>
///         Two kinds of job spend a client's money against one pull request, and both must count. The
///         accumulator's abstraction is widened to this subject rather than a second summand being bolted onto
///         a review-job-shaped contract, because the question a budget scope asks is about a pull request and an
///         increment, not about a table. A third unit of work later reaches the same scopes by describing itself
///         here, without the scopes learning its type.
///     </para>
///     <para>
///         <paramref name="UnitOfWorkId" /> identifies the asking row within its own table. Row identifiers are
///         unique across both tables, so excluding it is exact: each unit of work's cost lives in exactly one
///         row, is summed once, and is never counted twice when a review and a pass share an increment.
///     </para>
/// </remarks>
/// <param name="UnitOfWorkId">The identifier of the row asking, excluded from the totals it reads.</param>
/// <param name="ClientId">The client that pays.</param>
/// <param name="OrganizationUrl">Provider scope path the pull request lives under.</param>
/// <param name="ProjectId">Provider project, workspace, or namespace key.</param>
/// <param name="RepositoryId">Provider-native repository identifier.</param>
/// <param name="PullRequestId">Provider pull request number.</param>
/// <param name="IterationId">The increment this unit of work belongs to.</param>
public sealed record ReviewSpendSubject(
    Guid UnitOfWorkId,
    Guid ClientId,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId)
{
    /// <summary>Describes a review job as a spend subject.</summary>
    /// <param name="job">The review job.</param>
    public static ReviewSpendSubject For(ReviewJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new ReviewSpendSubject(
            job.Id,
            job.ClientId,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
    }

    /// <summary>Describes a thread pass as a spend subject.</summary>
    /// <param name="job">The thread pass.</param>
    public static ReviewSpendSubject For(ThreadPassJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new ReviewSpendSubject(
            job.Id,
            job.ClientId,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
    }
}
