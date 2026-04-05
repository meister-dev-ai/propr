// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Manages the ADO credential sub-resource attached to a client.</summary>
public interface IClientAdoCredentialRepository
{
    /// <summary>Removes credentials — the client falls back to the global identity.</summary>
    Task ClearAsync(Guid clientId, CancellationToken ct);

    /// <summary>
    ///     Returns the per-client ADO credentials, or <c>null</c> if none are configured.
    /// </summary>
    Task<ClientAdoCredentials?> GetByClientIdAsync(Guid clientId, CancellationToken ct);

    /// <summary>Creates or replaces the credentials for a client.</summary>
    Task UpsertAsync(Guid clientId, ClientAdoCredentials credentials, CancellationToken ct);
}
