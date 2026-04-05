// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Events;

/// <summary>
///     Raised by <c>PrCrawlService</c> when the per-thread state machine detects an
///     <c>active | unknown → resolved</c> transition.  Carries all content needed by
///     <c>ThreadMemoryService</c> to generate the resolution embedding without any
///     further ADO API calls.
/// </summary>
public sealed record ThreadResolvedDomainEvent(
    Guid ClientId,
    string RepositoryId,
    int PullRequestId,
    int ThreadId,
    string? FilePath,
    string? ChangeExcerpt,
    string CommentHistory,
    DateTimeOffset ObservedAt);
