// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One machine-scoped resource held by one connection of one provider add-in, for example a listener port.
/// </summary>
/// <remarks>
///     Recorded rather than held in memory so the arbitration covers the installation and not one replica. The
///     expiry is what keeps a replica that died holding a resource from holding it for good.
/// </remarks>
public sealed class ProviderResourceLeaseRecord
{
    public Guid Id { get; set; }

    /// <summary>The add-in that owns the resource name. Two add-ins naming one resource do not contend.</summary>
    public string AddInKey { get; set; } = string.Empty;

    /// <summary>The resource, named by the add-in and unique within it.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>The connection currently holding the resource.</summary>
    public Guid ConnectionProfileId { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }

    /// <summary>When the lease lapses whether or not it was released.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
