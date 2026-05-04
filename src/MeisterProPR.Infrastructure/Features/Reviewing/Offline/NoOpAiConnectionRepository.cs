// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     No-op AI connection repository for offline Reviewing composition.
/// </summary>
public sealed class NoOpAiConnectionRepository : IAiConnectionRepository
{
    public Task<IReadOnlyList<AiConnectionDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AiConnectionDto>>([]);

    public Task<AiConnectionDto?> GetActiveForClientAsync(Guid clientId, CancellationToken ct = default)
        => Task.FromResult<AiConnectionDto?>(null);

    public Task<AiConnectionDto?> GetByIdAsync(Guid connectionId, CancellationToken ct = default)
        => Task.FromResult<AiConnectionDto?>(null);

    public Task<AiConnectionDto> AddAsync(Guid clientId, AiConnectionWriteRequestDto request, CancellationToken ct = default)
        => throw new NotSupportedException("Offline Reviewing composition does not persist AI connection profiles.");

    public Task<bool> UpdateAsync(Guid connectionId, AiConnectionWriteRequestDto request, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> DeleteAsync(Guid connectionId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> ActivateAsync(Guid connectionId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> DeactivateAsync(Guid connectionId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> SaveVerificationAsync(Guid connectionId, AiVerificationResultDto verification, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<AiConnectionDto?> GetForTierAsync(Guid clientId, AiConnectionModelCategory tier, CancellationToken ct = default)
        => Task.FromResult<AiConnectionDto?>(null);

    public Task<AiResolvedPurposeBindingDto?> GetActiveBindingForPurposeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default)
        => Task.FromResult<AiResolvedPurposeBindingDto?>(null);
}
