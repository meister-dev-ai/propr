// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     The single question every Code Insights collection path asks before it does anything: may this
///     client's quality facts be collected at all?
/// </summary>
/// <remarks>
///     Two gates, both of which must be open: the installation must hold the commercial Code Insights
///     capability, and the client must have opted in. Every collection path (finding materialisation,
///     type classification, disposition back-tracking, miss harvesting, memory keywords) consults this
///     first, so that no record is written and no model token is spent behind a closed gate.
///     It fails closed: anything it cannot determine means "do not collect".
/// </remarks>
public interface ICodeInsightsCollectionGate
{
    /// <summary>
    ///     Returns whether Code Insights collection may run for <paramref name="clientId" /> right now.
    ///     Never throws: an error resolving either gate is reported as closed.
    /// </summary>
    Task<bool> IsCollectionEnabledAsync(Guid clientId, CancellationToken ct = default);
}
