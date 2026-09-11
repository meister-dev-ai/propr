// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     Observes a finished pull request's human threads once, so a judgement taken while a thread was open can
///     be revised before the measurement is sealed.
/// </summary>
/// <remarks>
///     <para>
///         Two paths seal a finished pull request: lifecycle synchronization, which sees the close as it
///         happens, and the seal sweep, which finds pull requests whose close nobody saw. Both call this first,
///         because the seal counts only judgements already recorded and never moves once written.
///     </para>
///     <para>
///         Implemented outside the code-insight module, unlike the other ports here. Deciding whether a comment
///         is ProPR's own needs the posted-comment provenance and the thread-ownership resolver, and judging
///         ProPR's own thread as a human miss would charge the reviewer for a finding it raised.
///     </para>
/// </remarks>
public interface ICodeInsightCloseObserver
{
    /// <summary>
    ///     Fetches the pull request's threads and hands each to the miss harvester. Best-effort: a provider or
    ///     store failure is logged and swallowed, because neither the seal nor the lifecycle work behind it may
    ///     be lost to a failed observation. Cancellation is the exception and propagates, so a caller shutting
    ///     down stops instead of sealing from judgements it was told to stop gathering.
    /// </summary>
    /// <param name="key">The pull request that finished.</param>
    /// <param name="providerScopePath">Provider organisation or host path the pull request lives under.</param>
    /// <param name="providerProjectKey">Provider project, workspace, or namespace key.</param>
    /// <param name="ct">Cancels the observation.</param>
    Task ObserveAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        CancellationToken ct = default);
}
