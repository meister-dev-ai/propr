// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     Records what became of a finding when its review thread resolves.
/// </summary>
/// <remarks>
///     It consumes the same thread-resolved event thread-memory does, as a sibling rather than a
///     modification. That separation matters: thread-memory deliberately refuses to store some resolutions
///     (a close with no corroborating change teaches a future review to discard a still-valid finding), and
///     those are precisely the cases a quality metric most needs recorded. A finding therefore gets a
///     disposition even where no memory is written.
///     Best-effort, like every other collection path: it never throws into the crawl.
/// </remarks>
public interface ICodeInsightDispositionService
{
    /// <summary>
    ///     Records the disposition for the finding the resolved thread belongs to. A thread that does not
    ///     correspond to a collected finding (raised before collection was enabled, or authored by a human)
    ///     is skipped. Re-observing a resolved thread leaves an already-decided disposition untouched.
    /// </summary>
    Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default);
}
