// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Events;

/// <summary>
///     Raised by <c>PrCrawlService</c> when the per-thread state machine detects a
///     <c>resolved → active</c> transition.
/// </summary>
public sealed record ThreadReopenedDomainEvent(
    Guid ClientId,
    string RepositoryId,
    int PullRequestId,
    int ThreadId,
    DateTimeOffset ObservedAt);
