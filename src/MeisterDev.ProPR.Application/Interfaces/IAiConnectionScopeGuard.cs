// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Enforces that a configuration referencing an AI connection profile by id stays inside its own tenant.
///     Connection profiles are looked up globally by id (a tenant-scoped profile must resolve without being
///     re-scoped to the requesting client), so the tenant boundary is not implied by the lookup and has to be
///     checked explicitly. Centralized here because the same rule applies at configuration time, when a
///     reference is written, and at resolution time, before any credential is used.
/// </summary>
public interface IAiConnectionScopeGuard
{
    /// <summary>
    ///     Returns <see langword="null" /> when <paramref name="connection" /> may be referenced from
    ///     <paramref name="referencingTenantId" />, or a user-facing reason when the reference must be refused.
    ///     Refuses when the connection's owning tenant cannot be established, so an unowned or orphaned profile
    ///     is never treated as unrestricted.
    /// </summary>
    /// <param name="connection">The referenced connection profile.</param>
    /// <param name="referencingTenantId">The tenant that owns the configuration making the reference.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> ValidateAsync(AiConnectionDto connection, Guid referencingTenantId, CancellationToken ct = default);
}
