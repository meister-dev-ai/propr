// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for per-client AI connection configurations.</summary>
public interface IAiConnectionRepository
{
    /// <summary>Returns all AI connections for the given client.</summary>
    Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns the active AI connection for the given client, or null if none is active.</summary>
    Task<AiConnectionDto?> GetActiveForClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns the AI connection by ID, or null if not found.</summary>
    Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Adds a new AI connection. Returns the created DTO.</summary>
    Task<AiConnectionDto> AddAsync(
        Guid clientId,
        string displayName,
        string endpointUrl,
        IReadOnlyList<string> models,
        string? apiKey,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities = null,
        AiConnectionModelCategory? modelCategory = null,
        CancellationToken ct = default);

    /// <summary>Updates non-null fields of an existing connection. Returns false if not found.</summary>
    Task<bool> UpdateAsync(
        Guid connectionId,
        string? displayName,
        string? endpointUrl,
        IReadOnlyList<string>? models,
        string? apiKey,
        IReadOnlyList<AiConnectionModelCapabilityDto>? modelCapabilities,
        CancellationToken ct = default);

    /// <summary>Deletes the given connection. Returns false if not found.</summary>
    Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    ///     Activates the specified connection with the given model, deactivating all other
    ///     connections for the same client in a single transaction.
    ///     Returns false if the connection is not found or the model is not in the connection's model list.
    /// </summary>
    Task<bool> ActivateAsync(Guid connectionId, string model, CancellationToken ct = default);

    /// <summary>Deactivates the specified connection. Returns false if not found.</summary>
    Task<bool> DeactivateAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the connection tagged with the specified <paramref name="tier" /> for the given client,
    ///     or <see langword="null" /> if no such connection exists. The caller falls back to the active default.
    /// </summary>
    Task<AiConnectionDto?> GetForTierAsync(Guid clientId, AiConnectionModelCategory tier, CancellationToken ct = default);
}
