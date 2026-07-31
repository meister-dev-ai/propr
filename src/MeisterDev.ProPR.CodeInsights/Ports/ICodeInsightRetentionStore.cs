// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>
///     Deletion boundary for collected code-insight data.
/// </summary>
/// <remarks>
///     Deleting collected evidence is a different kind of act from writing it, and the only consumer is a
///     background worker. Keeping it apart means nothing that collects data holds a handle that can erase it.
/// </remarks>
public interface ICodeInsightRetentionStore
{
    /// <summary>
    ///     Deletes pull-request aggregates whose last activity is strictly older than
    ///     <paramref name="cutoff" />, cascading to their findings. Returns the number of aggregates removed.
    /// </summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    ///     Deletes every collected pull-request aggregate for <paramref name="clientId" />, cascading to
    ///     their findings. Returns the number of aggregates removed.
    /// </summary>
    Task<int> PurgeForClientAsync(Guid clientId, CancellationToken ct = default);
}
