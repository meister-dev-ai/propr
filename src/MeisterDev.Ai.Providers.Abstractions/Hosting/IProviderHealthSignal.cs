// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Reports what a family learned about its own connection's credential.
/// </summary>
/// <remarks>
///     The value set is the host's and is closed, so a family names a member and cannot invent one. What it
///     reports is one input among several: a fresh verification outranks it, and it outranks what the retry stage
///     concluded from a failed call. It never stands in for verification, which is still what activates a
///     connection.
/// </remarks>
public interface IProviderHealthSignal
{
    /// <summary>Reports the credential state of the connection this handle is bound to.</summary>
    /// <param name="health">The state, from the host's closed set.</param>
    /// <param name="cause">
    ///     What the family observed, shown to an operator beside the state. Capped and scrubbed by the host
    ///     before it reaches a surface.
    /// </param>
    /// <param name="ct">Cancels the report.</param>
    Task ReportAsync(AiCredentialHealth health, string? cause = null, CancellationToken ct = default);
}
