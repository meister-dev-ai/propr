// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

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

    public Task<bool> GetIncludeLinkedItemsInContextEnabledAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<CommentSeverity> GetMinimumSeverityToPostAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(CommentSeverity.Info);
    }

    public Task<IReadOnlyList<CommentSeverity>> GetAutoResolveSeveritiesAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<CommentSeverity>>([]);
    }

    public Task<IReadOnlyList<ReviewPassSpec>> GetReviewPassesAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ReviewPassSpec>>([]);
    }

    public Task<ReviewReasoningEffort> GetBaselineReasoningEffortAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(ReviewReasoningEffort.None);
    }

    public Task<string?> GetDefaultReviewPipelineProfileIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<Guid?> GetTenantIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<Guid?>(null);
    }
}
