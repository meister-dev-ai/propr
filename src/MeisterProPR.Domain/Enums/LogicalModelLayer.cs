// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Which scope layer resolved a logical model for a client. Client overrides take precedence over the tenant
///     catalog; this records which one actually matched, for the review protocol/trace.
/// </summary>
public enum LogicalModelLayer
{
    /// <summary>A per-client override shadowed (or stood in for) the tenant entry.</summary>
    ClientOverride = 0,

    /// <summary>The tenant-catalog entry resolved the role (no client override present).</summary>
    TenantCatalog = 1,
}
