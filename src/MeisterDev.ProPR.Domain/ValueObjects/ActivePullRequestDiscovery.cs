// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     What one discovery tick found, and whether it covered everything it was asked about.
/// </summary>
/// <remarks>
///     <c>IsComplete</c> exists because a partial result has the same shape as a complete one. Discovery
///     isolates a repository that is throttled, deleted or no longer visible so the remaining repositories are
///     still read. A caller that advanced its watermark over such a result would never query that window
///     again, and a mention posted in it would never be answered.
/// </remarks>
/// <param name="PullRequests">The pull requests read, however much of the query they cover.</param>
/// <param name="IsComplete">
///     Whether every claimed repository in the query was read. False when one was skipped, when a throttle
///     ended the tick early, or when the connection could not be opened at all.
/// </param>
public sealed record ActivePullRequestDiscovery(
    IReadOnlyList<ActivePullRequestRef> PullRequests,
    bool IsComplete)
{
    /// <summary>A tick that covered everything it was asked about and found nothing.</summary>
    public static ActivePullRequestDiscovery Empty { get; } = new([], true);

    /// <summary>A tick that read nothing because it could not, so its window stays uncovered.</summary>
    public static ActivePullRequestDiscovery Failed { get; } = new([], false);
}
