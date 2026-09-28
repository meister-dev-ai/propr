// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.ProPR.Application.Features.Clients.Contracts;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Repository for per-client AI connection configurations.</summary>
public interface IAiConnectionRepository
{
    /// <summary>Verifies an unsaved candidate and atomically promotes it if the original profile remains current.</summary>
    Task<AiVerifiedUpdateResult> VerifyUpdateAsync(
        Guid clientId, AiConnectionDto original,
        AiConnectionWriteRequestDto request, Func<AiConnectionDto, CancellationToken, Task<AiVerificationResultDto>> verify,
        CancellationToken ct = default, Guid? authorizedTenantId = null);

    /// <summary>Applies all workspace purpose selections together after validating ownership and effective routing.</summary>
    Task<AiConfigurationResult> SelectPurposesAsync(
        Guid clientId, AiWorkspacePurposeSelection selection, CancellationToken ct = default, Guid? authorizedTenantId = null);

    /// <summary>Returns all AI connection profiles for the given client.</summary>
    Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns all tenant-scoped AI connection profiles for the given tenant (inherited by its clients).</summary>
    Task<IReadOnlyList<AiConnectionDto>> GetByTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Returns the active AI connection profile for the given client, or null if none is active.</summary>
    Task<AiConnectionDto?> GetActiveForClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns the AI connection profile by ID, or null if not found.</summary>
    Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Loads distinct referenced connections in one database read; callers enforce the reference scope.</summary>
    Task<IReadOnlyList<AiConnectionDto>> GetByIdsAsync(IReadOnlyCollection<Guid> connectionIds, CancellationToken ct = default);

    /// <summary>Adds a new AI connection profile. Returns the created DTO.</summary>
    Task<AiConnectionDto> AddAsync(Guid clientId, AiConnectionWriteRequestDto request, CancellationToken ct = default);

    /// <summary>Adds a new tenant-scoped AI connection profile (inherited by the tenant's clients). Returns the created DTO.</summary>
    Task<AiConnectionDto> AddTenantAsync(Guid tenantId, AiConnectionWriteRequestDto request, CancellationToken ct = default);

    /// <summary>Replaces the persisted content of an existing AI connection profile.</summary>
    Task<bool> UpdateAsync(Guid connectionId, AiConnectionWriteRequestDto request, CancellationToken ct = default);

    /// <summary>Deletes the given AI connection profile. Returns false if not found.</summary>
    Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Activates the specified AI connection profile and deactivates any others for the same client.</summary>
    Task<AiConnectionActivationResultDto> ActivateAsync(Guid connectionId, CancellationToken ct = default);

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

    /// <summary>
    ///     Resolves a chat-capable configured model by its identifier across the client's connection profiles,
    ///     synthesizing a purpose-neutral binding (reusing an existing enabled binding's protocol mode for that model
    ///     when present, otherwise <see cref="ProviderDeclaredProtocolModes.Auto" />). Returns
    ///     <see langword="null" /> when the model
    ///     is not found on any of the client's profiles or does not support chat workloads.
    /// </summary>
    Task<AiResolvedPurposeBindingDto?> GetModelBindingAsync(
        Guid clientId,
        Guid configuredModelId,
        CancellationToken ct = default);
}
