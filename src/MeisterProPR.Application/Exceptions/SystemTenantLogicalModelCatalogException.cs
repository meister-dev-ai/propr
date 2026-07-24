// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when a tenant-catalog logical model is created for the system tenant. The system tenant has no
///     tenant-wide catalog layer — it stores per-client overrides only.
/// </summary>
public sealed class SystemTenantLogicalModelCatalogException : Exception
{
    /// <inheritdoc />
    public SystemTenantLogicalModelCatalogException()
        : base("The system tenant has no tenant-catalog layer; create a per-client override instead.")
    {
    }
}
