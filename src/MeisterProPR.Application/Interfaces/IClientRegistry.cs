// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Provides persisted per-client review settings.
///     This no longer participates in legacy client-key lookup or rotation.
/// </summary>
public interface IClientRegistry
{
    /// <summary>
    ///     Returns the configured provider reviewer-trigger identity for the given client and provider host,
    ///     or <see langword="null" /> when no active connection or reviewer identity is configured.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="host">Normalized provider host for the active connection lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReviewerIdentity?> GetReviewerIdentityAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the configured provider reviewer-trigger identity when present, or a provider-derived fallback identity
    ///     used only for automated trigger evaluation when the provider supports one.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="host">Normalized provider host for the active connection lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReviewerIdentity?> GetEffectiveReviewerIdentityAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="CommentResolutionBehavior" /> configured for the given client,
    ///     or <see cref="CommentResolutionBehavior.Silent" /> if not found.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the custom AI system message configured for the given client, or <see langword="null" /> if not set.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetCustomSystemMessageAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether newly generated review comments should be published back to SCM for the given client.
    ///     Defaults to <see langword="true" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetScmCommentPostingEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether ProRV should execute for the given client.
    ///     Defaults to <see langword="false" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetProRvEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether evidence-backed local verification should run for the given client.
    ///     Defaults to <see langword="false" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetEvidenceBackedVerificationEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether multi-pass union generation should run for the given client.
    ///     Defaults to <see langword="false" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetMultiPassUnionEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the ordered per-client review-pass list — each configured model (in ordinal order) with its optional
    ///     specialist lens — that runs one additional multi-pass union pass after the implicit tier baseline. Empty
    ///     when the client has configured no additional passes (multi-pass union then degrades to a single baseline pass).
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ReviewPassSpec>> GetReviewPassesAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the default review strategy configured for the given client, or <see langword="null" /> if not set.
    /// </summary>
    Task<ReviewStrategy?> GetDefaultReviewStrategyAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the default review pipeline profile configured for the given client, or <see langword="null" /> if not set.
    /// </summary>
    Task<string?> GetDefaultReviewPipelineProfileIdAsync(Guid clientId, CancellationToken ct = default);
}
