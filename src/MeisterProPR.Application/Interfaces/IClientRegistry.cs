// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
    ///     Returns the configured provider reviewer identity for the given client and provider host,
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
}
