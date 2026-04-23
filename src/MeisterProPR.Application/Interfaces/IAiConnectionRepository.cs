// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for per-client AI connection configurations.</summary>
public interface IAiConnectionRepository
{
    /// <summary>Returns all AI connection profiles for the given client.</summary>
    Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns the active AI connection profile for the given client, or null if none is active.</summary>
    Task<AiConnectionDto?> GetActiveForClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns the AI connection profile by ID, or null if not found.</summary>
    Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Adds a new AI connection profile. Returns the created DTO.</summary>
    Task<AiConnectionDto> AddAsync(Guid clientId, AiConnectionWriteRequestDto request, CancellationToken ct = default);

    /// <summary>Replaces the persisted content of an existing AI connection profile.</summary>
    Task<bool> UpdateAsync(Guid connectionId, AiConnectionWriteRequestDto request, CancellationToken ct = default);

    /// <summary>Deletes the given AI connection profile. Returns false if not found.</summary>
    Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Activates the specified AI connection profile and deactivates any others for the same client.</summary>
    Task<bool> ActivateAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Deactivates the specified AI connection profile. Returns false if not found.</summary>
    Task<bool> DeactivateAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Persists the latest verification result for a profile. Returns false if not found.</summary>
    Task<bool> SaveVerificationAsync(Guid connectionId, AiVerificationResultDto verification, CancellationToken ct = default);

    /// <summary>
    ///     Compatibility lookup retained for legacy tests. New runtime code should use
    ///     <see cref="GetActiveBindingForPurposeAsync" /> instead.
    /// </summary>
    Task<AiConnectionDto?> GetForTierAsync(Guid clientId, AiConnectionModelCategory tier, CancellationToken ct = default);

    /// <summary>
    ///     Resolves the active profile and purpose binding for the requested AI purpose,
    ///     or <see langword="null" /> if no valid binding exists.
    /// </summary>
    Task<AiResolvedPurposeBindingDto?> GetActiveBindingForPurposeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default);
}
