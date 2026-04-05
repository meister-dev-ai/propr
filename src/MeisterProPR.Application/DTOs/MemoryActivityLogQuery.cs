// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Query parameters for <see cref="MeisterProPR.Application.Interfaces.IMemoryActivityLog.QueryAsync" />.</summary>
public sealed record MemoryActivityLogQuery(
    int? ThreadId = null,
    int? PullRequestId = null,
    string? RepositoryId = null,
    MemoryActivityAction? Action = null,
    DateTimeOffset? FromDate = null,
    DateTimeOffset? ToDate = null,
    int Page = 1,
    int PageSize = 50);
