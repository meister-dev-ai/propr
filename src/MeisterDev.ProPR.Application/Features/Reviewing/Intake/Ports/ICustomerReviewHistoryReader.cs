// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;

/// <summary>Reads bounded persisted review metadata for one client.</summary>
public interface ICustomerReviewHistoryReader
{
    /// <summary>Returns a client-scoped page and its total matching review count.</summary>
    Task<CustomerReviewHistory> GetAsync(Guid clientId, int page, int pageSize, JobStatus? status, CancellationToken ct);
}

/// <summary>A page of persisted client reviews.</summary>
public sealed record CustomerReviewHistory(long TotalCount, int Page, int PageSize, IReadOnlyList<CustomerReviewHistoryItem> Items);

/// <summary>Scalar persisted review metadata and its finding count.</summary>
public sealed record CustomerReviewHistoryItem(
    Guid Id,
    JobStatus Status,
    ScmProvider Provider,
    string Repository,
    int PullRequestNumber,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    int FindingCount);
