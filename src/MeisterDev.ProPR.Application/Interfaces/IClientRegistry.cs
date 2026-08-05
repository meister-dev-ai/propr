// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

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
    ///     Returns the client's configured output language as an IETF BCP 47 tag. Every prompt that emits
    ///     reviewer-facing prose states it, so a review reads in one language on all of its surfaces.
    ///     Falls back to <see cref="ReviewOutputLanguage.Default" /> when the client does not exist or stores
    ///     nothing usable.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetOutputLanguageAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether newly generated review comments should be published back to SCM for the given client.
    ///     Defaults to <see langword="true" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetScmCommentPostingEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether evidence-backed local verification should run for the given client.
    ///     Defaults to <see langword="false" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetEvidenceBackedVerificationEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether the client opted into language-robust, evidence-based comment screening.
    ///     Defaults to <see langword="false" /> if the client does not exist.
    /// </summary>
    Task<bool> GetLanguageRobustScreeningEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether multi-pass union generation should run for the given client.
    ///     Defaults to <see langword="false" /> if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetMultiPassUnionEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether the client has opted in to Code Insights collection.
    ///     Defaults to <see langword="false" /> if the client does not exist, so an unknown client never
    ///     starts collecting. The commercial capability gate applies in addition to this flag.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetCodeInsightsCollectionEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether linked work items / issues should be fetched and included in the review context
    ///     for the given client. Defaults to <see langword="true" /> if the client does not exist.
    /// </summary>
    Task<bool> GetIncludeLinkedItemsInContextEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns whether an automatic trigger reviews every pushed update to a pull request for the given client.
    ///     Defaults to <see langword="false" /> if the client does not exist, so an unknown client is reviewed at the
    ///     first revision seen and no further.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> GetReviewEveryIncrementEnabledAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the minimum severity a finding must have for its comment to be published to the SCM provider.
    ///     Findings ranked below it are retained in the persisted review result but not posted. Defaults to
    ///     <see cref="CommentSeverity.Info" /> (publish everything) if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CommentSeverity> GetMinimumSeverityToPostAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the severities whose published comments are posted already resolved (with an explanatory note)
    ///     for the given client. Defaults to an empty set (nothing auto-resolved) if the client does not exist.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<CommentSeverity>> GetAutoResolveSeveritiesAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the ordered per-client review-pass list — each configured model (in ordinal order) with its optional
    ///     specialist lens — that runs one additional multi-pass union pass after the implicit tier baseline. Empty
    ///     when the client has configured no additional passes (multi-pass union then degrades to a single baseline pass).
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ReviewPassSpec>> GetReviewPassesAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the reasoning effort configured at the client level for the implicit tier baseline review pass.
    ///     Defaults to <see cref="ReviewReasoningEffort.None" /> (no effort sent) if the client does not exist or
    ///     has not opted in.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReviewReasoningEffort> GetBaselineReasoningEffortAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the default review pipeline profile configured for the given client, or <see langword="null" /> if not set.
    /// </summary>
    Task<string?> GetDefaultReviewPipelineProfileIdAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the tenant owning the given client, or <see langword="null" /> when the client does not exist or
    ///     is not assigned to a tenant. Callers enforcing a tenant boundary must treat <see langword="null" /> as
    ///     "boundary cannot be established" and refuse, rather than as "no boundary applies".
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Guid?> GetTenantIdAsync(Guid clientId, CancellationToken ct = default);
}
