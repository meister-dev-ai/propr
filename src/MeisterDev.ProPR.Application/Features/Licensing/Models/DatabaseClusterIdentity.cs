// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What the database says about itself and the cluster it lives in.
/// </summary>
/// <param name="SystemIdentifier">
///     The cluster's system identifier as a decimal string, or <see langword="null" /> when the role may not
///     read it.
/// </param>
/// <param name="DatabaseName">The current database's name, or <see langword="null" /> when it could not be read.</param>
/// <param name="DatabaseOid">
///     The current database's object identifier, or <see langword="null" /> when it could not be read.
/// </param>
public sealed record DatabaseClusterIdentity(string? SystemIdentifier, string? DatabaseName, long? DatabaseOid)
{
    /// <summary>Nothing about the database could be read.</summary>
    public static DatabaseClusterIdentity Unknown { get; } = new(null, null, null);
}
