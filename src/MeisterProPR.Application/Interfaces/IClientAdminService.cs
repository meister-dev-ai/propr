// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Admin CRUD operations for managing clients.</summary>
public interface IClientAdminService
{
    /// <summary>Returns all clients ordered by creation date descending.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ClientDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns a single client by ID, or <c>null</c> if not found.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientDto?> GetByIdAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Creates a new active client and returns its data.</summary>
    /// <param name="tenantId">Tenant that owns the client.</param>
    /// <param name="displayName">Human-readable name for the client.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientDto> CreateAsync(Guid tenantId, string displayName, CancellationToken ct = default);

    /// <summary>
    ///     Applies partial updates to a client.
    ///     Returns the updated client, or <c>null</c> if not found.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="isActive">When non-null, sets the active flag.</param>
    /// <param name="displayName">When non-null, replaces the display name.</param>
    /// <param name="commentResolutionBehavior">When non-null, sets the comment resolution behavior.</param>
    /// <param name="customSystemMessage">
    ///     When non-null, updates the custom AI system message.
    ///     Pass an empty string to clear an existing value (sets the stored value to <see langword="null" />).
    /// </param>
    /// <param name="defaultReviewPipelineProfileId">
    ///     When non-null, updates the default review pipeline profile for newly submitted jobs.
    ///     Pass an empty string to clear an existing value (sets the stored value to <see langword="null" />).
    /// </param>
    /// <param name="scmCommentPostingEnabled">When non-null, sets whether new review comments are posted back to SCM.</param>
    /// <param name="enableEvidenceBackedVerification">When non-null, sets whether evidence-backed local verification runs for new reviews.</param>
    /// <param name="enableLanguageRobustScreening">When non-null, sets whether language-robust evidence-based comment screening runs for new reviews.</param>
    /// <param name="enableMultiPassUnion">When non-null, sets whether multi-pass union generation runs for new reviews.</param>
    /// <param name="includeLinkedItemsInContext">When non-null, sets whether linked work items / issues are included in the review context.</param>
    /// <param name="reviewPasses">
    ///     When non-null, replaces the client's ordered review-pass list wholesale (an empty list clears it).
    ///     Each entry binds one additional multi-pass union pass to a configured model.
    /// </param>
    /// <param name="baselineReasoningEffort">
    ///     When non-null, sets the client-level reasoning effort for the implicit tier baseline review pass.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientDto?> PatchAsync(
        Guid clientId,
        bool? isActive,
        string? displayName,
        CommentResolutionBehavior? commentResolutionBehavior = null,
        string? customSystemMessage = null,
        string? defaultReviewPipelineProfileId = null,
        bool? scmCommentPostingEnabled = null,
        bool? enableEvidenceBackedVerification = null,
        bool? enableLanguageRobustScreening = null,
        bool? enableMultiPassUnion = null,
        bool? includeLinkedItemsInContext = null,
        IReadOnlyList<ReviewPassDto>? reviewPasses = null,
        ReviewReasoningEffort? baselineReasoningEffort = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes a client and all its crawl configurations.
    ///     Returns <c>false</c> if the client was not found.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DeleteAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns <c>true</c> when a client with <paramref name="clientId" /> exists.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns clients with the given IDs. Silently omits IDs that do not exist.</summary>
    /// <param name="ids">Collection of client identifiers to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ClientDto>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>Returns the most recent provider-connection audit entries for the given client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="take">Maximum number of entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ProviderConnectionAuditEntryDto>> GetProviderConnectionAuditTrailAsync(
        Guid clientId,
        int take = 20,
        CancellationToken ct = default);

    /// <summary>Returns the published selectable review pipeline profiles for client administration.</summary>
    Task<IReadOnlyList<ReviewPipelineProfile>> GetSelectableReviewPipelineProfilesAsync(CancellationToken ct = default);
}
