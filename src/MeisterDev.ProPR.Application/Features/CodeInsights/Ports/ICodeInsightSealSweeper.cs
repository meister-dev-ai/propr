// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Seals the correctness measurement of pull requests whose closure the synchronization path never observed.
/// </summary>
/// <remarks>
///     <para>
///         The two activation sources do not observe closure equally. Webhooks report a merge or a close directly,
///         so those pull requests seal as they finish. The crawl only status-checks pull requests that still have
///         an active review job, so a pull request merged <em>after</em> its review had finished is never
///         examined, and for a crawl-only installation that is most of them.
///     </para>
///     <para>
///         This sweep closes that gap by asking the provider about collected pull requests that have gone quiet
///         and have no measurement yet. It is the difference between a correctness metric that covers a client's
///         work and one that covers only the pull requests that happened to end mid-review.
///     </para>
/// </remarks>
public interface ICodeInsightSealSweeper
{
    /// <summary>
    ///     Examines up to <paramref name="maxPullRequests" /> collected pull requests that have had no collection
    ///     activity for <paramref name="idleFor" /> and carry no sealed measurement, and seals the ones the
    ///     provider reports as finished. Returns how many were sealed.
    /// </summary>
    /// <remarks>
    ///     One provider call per candidate, so the batch is bounded and the idle threshold is generous. Candidates
    ///     are taken most-recently-active first: a pull request that closed last week is worth far more to a
    ///     current metric than one that has been quiet for a year, and the ancient ones are on their way out of
    ///     retention anyway.
    /// </remarks>
    Task<int> SweepAsync(int maxPullRequests, TimeSpan idleFor, CancellationToken ct = default);
}
