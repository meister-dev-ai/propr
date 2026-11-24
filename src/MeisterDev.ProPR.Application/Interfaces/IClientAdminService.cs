// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

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
    /// <param name="enableMultiPassUnion">When non-null, sets whether multi-pass union generation runs for new reviews.</param>
    /// <param name="includeLinkedItemsInContext">When non-null, sets whether linked work items / issues are included in the review context.</param>
    /// <param name="reviewPasses">
    ///     When non-null, replaces the client's ordered review-pass list wholesale (an empty list clears it).
    ///     Each entry binds one additional multi-pass union pass to a configured model.
    /// </param>
    /// <param name="baselineReasoningLevel">
    ///     When non-null, sets the client-level reasoning level for the implicit tier baseline review pass; an empty
    ///     string clears it. The baseline pass uses it when neither the purpose assignment nor the logical model of the
    ///     file's tier sets a level.
    /// </param>
    /// <param name="budgetConfig">
    ///     When non-null, replaces the client's USD budget caps wholesale; each cap within it may be null to clear
    ///     that individual limit (the per-field "omit means unchanged" convention cannot express clearing a single
    ///     nullable cap).
    /// </param>
    /// <param name="minimumSeverityToPost">
    ///     When non-null, sets the minimum severity a finding must have for its comment to be posted to the SCM provider.
    /// </param>
    /// <param name="autoResolveSeverities">
    ///     When non-null, replaces the set of severities whose comments are posted already resolved (an empty list
    ///     clears it).
    /// </param>
    /// <param name="outputLanguage">
    ///     When non-null, sets the IETF BCP 47 language tag ProPR writes this client's reviewer-facing prose in.
    /// </param>
    /// <param name="reviewEveryIncrementEnabled">
    ///     When non-null, sets whether an automatic trigger reviews every pushed update to a pull request rather than
    ///     only the first revision it sees.
    /// </param>
    /// <param name="findingBadgeStyle">When non-null, sets the metadata badge style for SCM finding comments.</param>
    /// <param name="withholdOutOfScopeFindings">
    ///     When non-null, sets whether findings in pre-existing code outside the pull request's changed lines are kept
    ///     off the pull request. They remain in the persisted review result either way.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="minimumRiskSeverityToPost">
    ///     When non-null, sets the lowest assessed risk a finding needs to be posted to the pull request.
    /// </param>
    /// <param name="withheldFindingTypes">
    ///     When non-null, sets the listed core finding types to <see cref="FindingPostDestination.ReviewOnly" />, and
    ///     posts every other type that had that destination as a comment again. A listed type that did not have that
    ///     destination before loses its keys with a qualifier, and a listed type that had it keeps them, so writing back
    ///     the list that was read changes nothing. Other entries of the destination map stay as they are. The parameter
    ///     serves API consumers that write the list of finding types that are not posted.
    /// </param>
    /// <param name="findingTypeDestinations">
    ///     When non-null, replaces the destination per finding type. Keys are core type slugs or core type slugs with
    ///     a qualifier. An empty map posts every finding as a comment.
    /// </param>
    /// <param name="maximumFindingsToPost">
    ///     The largest number of findings posted as comments on one pull request, or <see langword="null" /> for no limit.
    ///     Applied only when <paramref name="updateMaximumFindingsToPost" /> is <see langword="true" />.
    /// </param>
    /// <param name="updateMaximumFindingsToPost">
    ///     Whether <paramref name="maximumFindingsToPost" /> replaces the stored limit. A <see langword="null" /> limit
    ///     cannot mean "unchanged" here, because it clears the limit.
    /// </param>
    Task<ClientDto?> PatchAsync(
        Guid clientId,
        bool? isActive,
        string? displayName,
        CommentResolutionBehavior? commentResolutionBehavior = null,
        string? customSystemMessage = null,
        string? defaultReviewPipelineProfileId = null,
        bool? scmCommentPostingEnabled = null,
        bool? enableMultiPassUnion = null,
        bool? includeLinkedItemsInContext = null,
        IReadOnlyList<ReviewPassDto>? reviewPasses = null,
        string? baselineReasoningLevel = null,
        BudgetConfigDto? budgetConfig = null,
        CommentSeverity? minimumSeverityToPost = null,
        IReadOnlyList<CommentSeverity>? autoResolveSeverities = null,
        bool? codeInsightsCollectionEnabled = null,
        string? outputLanguage = null,
        bool? reviewEveryIncrementEnabled = null,
        bool? withholdOutOfScopeFindings = null,
        ReviewAdmissionPolicyDto? admissionPolicy = null,
        CancellationToken ct = default,
        FindingBadgeStyle? findingBadgeStyle = null,
        FindingRiskSeverity? minimumRiskSeverityToPost = null,
        IReadOnlyList<string>? withheldFindingTypes = null,
        IReadOnlyDictionary<string, FindingPostDestination>? findingTypeDestinations = null,
        int? maximumFindingsToPost = null,
        bool updateMaximumFindingsToPost = false);

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
