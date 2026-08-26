// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Reads what the database says about itself, for the parts of the profile that identify where the
///     installation's state lives.
/// </summary>
public interface IDatabaseClusterIdentityProbe
{
    /// <summary>
    ///     Reads the cluster's system identifier and the current database's name and object identifier.
    ///     <para>
    ///         A component the connected role may not read comes back absent rather than as a failure. The
    ///         cluster system identifier is the one this applies to in practice: the privilege to read it can be
    ///         taken away, and a managed PostgreSQL service may withhold it.
    ///     </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What could be read.</returns>
    Task<DatabaseClusterIdentity> ReadAsync(CancellationToken cancellationToken = default);
}
