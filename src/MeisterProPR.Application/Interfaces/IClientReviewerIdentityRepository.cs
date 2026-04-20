// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Persists the configured reviewer identity for one client provider connection.</summary>
public interface IClientReviewerIdentityRepository
{
    /// <summary>Returns the configured reviewer identity for one client provider connection.</summary>
    Task<ClientReviewerIdentityDto?> GetByConnectionIdAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default);

    /// <summary>Creates or replaces the configured reviewer identity for one client provider connection.</summary>
    Task<ClientReviewerIdentityDto?> UpsertAsync(
        Guid clientId,
        Guid connectionId,
        ScmProvider providerFamily,
        string externalUserId,
        string login,
        string displayName,
        bool isBot,
        CancellationToken ct = default);

    /// <summary>Clears the configured reviewer identity for one client provider connection.</summary>
    Task<bool> DeleteAsync(Guid clientId, Guid connectionId, CancellationToken ct = default);
}
