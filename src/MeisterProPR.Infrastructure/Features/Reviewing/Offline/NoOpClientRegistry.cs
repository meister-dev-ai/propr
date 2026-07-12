// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline placeholder for client-scoped review settings when live provider state is unavailable.
/// </summary>
public sealed class NoOpClientRegistry : IClientRegistry
{
    public Task<ReviewerIdentity?> GetReviewerIdentityAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        return Task.FromResult<ReviewerIdentity?>(null);
    }

    public Task<ReviewerIdentity?> GetEffectiveReviewerIdentityAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        return Task.FromResult<ReviewerIdentity?>(null);
    }

    public Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(CommentResolutionBehavior.Silent);
    }

    public Task<string?> GetCustomSystemMessageAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<bool> GetScmCommentPostingEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<bool> GetEvidenceBackedVerificationEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> GetLanguageRobustScreeningEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> GetMultiPassUnionEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<ReviewPassSpec>> GetReviewPassesAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ReviewPassSpec>>([]);
    }

    public Task<string?> GetDefaultReviewPipelineProfileIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }
}
