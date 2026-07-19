// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Events;

/// <summary>
///     Raised by <c>PrCrawlService</c> when the per-thread state machine detects an
///     <c>active | unknown → resolved</c> transition.  Carries all content needed by
///     <c>ThreadMemoryService</c> to generate the resolution embedding without any
///     further ADO API calls.
/// </summary>
/// <param name="Intent">
///     Provider-neutral meaning of the close: a deliberate human acceptance, or a mere claim that the
///     concern was fixed. Gates whether a code change must corroborate the close before it is stored.
/// </param>
/// <param name="CodeChangedSinceRaised">
///     Whether the anchored code changed since the finding was raised. A claimed fix is only trusted as
///     memory when this is <see cref="ThreadAnchorCodeChange.Changed" />.
/// </param>
public sealed record ThreadResolvedDomainEvent(
    Guid ClientId,
    string RepositoryId,
    int PullRequestId,
    long ThreadId,
    string? FilePath,
    string? ChangeExcerpt,
    string CommentHistory,
    DateTimeOffset ObservedAt,
    ThreadResolutionIntent Intent = ThreadResolutionIntent.ClaimsFix,
    ThreadAnchorCodeChange CodeChangedSinceRaised = ThreadAnchorCodeChange.Unknown);
