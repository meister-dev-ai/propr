// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages client registration, lookup, updates, and deletion.</summary>
[ApiController]
[Route("clients")]
public sealed class ClientsController(
    IClientAdminService clientAdminService,
    ITenantAdminService tenantAdminService,
    IClientTokenUsageRepository usageRepository,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private static ClientResponse ToClientResponse(ClientDto client, long? recentUsageTokens = null)
    {
        return new ClientResponse(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage,
            client.ScmCommentPostingEnabled,
            client.EnableEvidenceBackedVerification,
            client.EnableLanguageRobustScreening,
            client.EnableMultiPassUnion,
            client.IncludeLinkedItemsInContext,
            client.ReviewPassesOrEmpty
                .Select(pass => new ReviewPassEntry(pass.Ordinal, pass.ConfiguredModelId, pass.Lens, pass.Scope, pass.Shadow, pass.ReasoningEffort))
                .ToList(),
            client.BaselineReasoningEffort,
            client.TenantId,
            client.TenantSlug,
            client.TenantDisplayName,
            client.DefaultReviewPipelineProfileId,
            client.DefaultReviewPipelineProfileUpdatedAtUtc,
            recentUsageTokens,
            client.BudgetConfigOrEmpty);
    }

    private IActionResult? ValidateRequest(ValidationResult result)
    {
        if (result.IsValid)
        {
            return null;
        }

        foreach (var error in result.Errors)
        {
            this.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return this.ValidationProblem();
    }

    /// <summary>
    ///     Rejects a review-pass list that references a configured model the client cannot run: each entry's
    ///     <c>configuredModelId</c> must resolve to a chat-capable configured model on one of the client's own
    ///     connection profiles (any profile, active or not). An unknown id, another client's model, or an
    ///     embedding-only model yields a 400. Returns <see langword="null" /> when there is nothing to reject.
    /// </summary>
    private async Task<IActionResult?> ValidateReviewPassModelsAsync(
        Guid clientId,
        IReadOnlyList<ReviewPassEntry>? reviewPasses,
        IAiConnectionRepository aiConnectionRepository,
        CancellationToken ct)
    {
        if (reviewPasses is not { Count: > 0 })
        {
            return null;
        }

        foreach (var pass in reviewPasses)
        {
            var binding = await aiConnectionRepository.GetModelBindingAsync(clientId, pass.ConfiguredModelId, ct);
            if (binding is null)
            {
                this.ModelState.AddModelError(
                    nameof(PatchClientRequest.ReviewPasses),
                    $"Review pass model '{pass.ConfiguredModelId}' is not a chat-capable configured model on any of this client's connections.");
                return this.ValidationProblem();
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns null if the caller is a global admin.
    ///     Returns 403 Forbidden if the caller is authenticated but not admin.
    ///     Returns 401 Unauthorized if the caller is unauthenticated.
    /// </summary>
    private IActionResult? RequirePlatformAdmin()
    {
        return AuthHelpers.RequirePlatformAdmin(this.HttpContext);
    }

    private IActionResult? RequireClientAccess(Guid clientId, ClientRole minimumRole)
    {
        return AuthHelpers.RequireClientRole(this.HttpContext, clientId, minimumRole);
    }

    /// <summary>
    ///     Registers a new client. Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="request">Client registration details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Client registered.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is not a global admin.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateClient(
        [FromBody] CreateClientRequest request,
        [FromServices] IValidator<CreateClientRequest> validator,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (!AuthHelpers.IsAdmin(this.HttpContext))
        {
            var tenantAuth = request.TenantId == TenantCatalog.SystemTenantId
                ? this.StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have the required role for this tenant." })
                : AuthHelpers.RequireTenantRole(
                    this.HttpContext,
                    request.TenantId,
                    TenantRole.TenantAdministrator);
            if (tenantAuth is not null)
            {
                return tenantAuth;
            }
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (await tenantAdminService.GetByIdAsync(request.TenantId, ct) is null)
        {
            return this.NotFound();
        }

        try
        {
            var client = await clientAdminService.CreateAsync(
                request.TenantId,
                request.DisplayName,
                ct);

            return this.CreatedAtAction(
                nameof(this.GetClient),
                new { clientId = client.Id },
                ToClientResponse(client));
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Deletes a client and all its crawl configurations.
    ///     Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <response code="204">Client deleted.</response>
    /// <response code="401">Missing or invalid admin credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpDelete("{clientId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        var auth = this.RequirePlatformAdmin();
        if (auth is not null)
        {
            return auth;
        }

        var deleted = await clientAdminService.DeleteAsync(clientId);
        return deleted ? this.NoContent() : this.NotFound();
    }

    /// <summary>
    ///     Gets a single client by ID.
    ///     Requires valid user authentication and access to the requested client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClient(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        var client = await clientAdminService.GetByIdAsync(clientId, ct);
        if (client is null)
        {
            return this.NotFound();
        }

        if (!isAdmin && !AuthHelpers.GetClientRoles(this.HttpContext).ContainsKey(clientId))
        {
            return this.StatusCode(403, new { error = "Access denied." });
        }

        return this.Ok(ToClientResponse(client));
    }

    /// <summary>
    ///     Lists registered clients.
    ///     Admins receive all clients.
    ///     Non-admin users receive only clients for their assigned roles.
    ///     Requires a valid JWT bearer token or <c>X-User-Pat</c>.
    /// </summary>
    /// <response code="200">List of clients.</response>
    /// <response code="401">No valid credentials provided.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ClientResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetClients(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        // Trailing-30-day token usage per client for the directory's "USAGE (30D)" column, summed from the
        // per-client daily usage samples (the same source the usage dashboard reads).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var usageByClient = await usageRepository.GetRecentTotalsByClientAsync(today.AddDays(-30), today, ct);

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        if (isAdmin)
        {
            var all = await clientAdminService.GetAllAsync(ct);
            return this.Ok(all.Select(c => ToClientResponse(c, usageByClient.GetValueOrDefault(c.Id))).ToList());
        }

        var clientIds = AuthHelpers.GetClientRoles(this.HttpContext).Keys.ToList();
        if (clientIds.Count == 0)
        {
            return this.Ok(Array.Empty<ClientResponse>());
        }

        var scoped = await clientAdminService.GetByIdsAsync(clientIds, ct);
        return this.Ok(scoped.Select(c => ToClientResponse(c, usageByClient.GetValueOrDefault(c.Id))).ToList());
    }

    /// <summary>
    ///     Updates one or more fields of a client (display name, active status, review settings, custom system message).
    ///     Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid admin credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpPatch("{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchClient(
        Guid clientId,
        [FromBody] PatchClientRequest request,
        [FromServices] IValidator<PatchClientRequest> validator,
        [FromServices] IAiConnectionRepository aiConnectionRepository,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var reviewPassValidation = await this.ValidateReviewPassModelsAsync(clientId, request.ReviewPasses, aiConnectionRepository, ct);
        if (reviewPassValidation is not null)
        {
            return reviewPassValidation;
        }

        if (request.BudgetConfig is not null)
        {
            // Budgeting is a licensed capability; setting caps requires it to be enabled for the installation.
            var budgetCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
                licensingCapabilityService,
                PremiumCapabilityKey.Budgeting,
                ct);
            if (budgetCapability is not null)
            {
                return new PremiumFeatureUnavailableResult(budgetCapability);
            }
        }

        var client = await clientAdminService.PatchAsync(
            clientId,
            request.IsActive,
            request.DisplayName,
            request.CommentResolutionBehavior,
            request.CustomSystemMessage,
            null,
            request.ScmCommentPostingEnabled,
            request.EnableEvidenceBackedVerification,
            request.EnableLanguageRobustScreening,
            request.EnableMultiPassUnion,
            request.IncludeLinkedItemsInContext,
            request.ReviewPasses?
                .Select(pass => new ReviewPassDto(pass.Ordinal, pass.ConfiguredModelId, pass.Lens, pass.Scope, pass.Shadow, pass.ReasoningEffort))
                .ToList(),
            request.BaselineReasoningEffort,
            request.BudgetConfig,
            ct);
        return client is null ? this.NotFound() : this.Ok(ToClientResponse(client));
    }
}

/// <summary>Client response — key, ADO secret, and credential metadata are never included.</summary>
public sealed record ClientResponse(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    CommentResolutionBehavior CommentResolutionBehavior,
    string? CustomSystemMessage,
    bool ScmCommentPostingEnabled,
    bool EnableEvidenceBackedVerification,
    bool EnableLanguageRobustScreening,
    bool EnableMultiPassUnion,
    bool IncludeLinkedItemsInContext,
    IReadOnlyList<ReviewPassEntry> ReviewPasses,
    ReviewReasoningEffort BaselineReasoningEffort,
    Guid? TenantId,
    string? TenantSlug,
    string? TenantDisplayName,
    string? DefaultReviewPipelineProfileId,
    DateTimeOffset? DefaultReviewPipelineProfileUpdatedAtUtc,
    long? RecentUsageTokens = null,
    BudgetConfigDto? BudgetConfig = null);

/// <summary>One entry in a client's ordered review-pass list: an additional multi-pass union pass bound to a model.</summary>
/// <param name="Ordinal">Zero-based position of this pass after the implicit tier baseline pass.</param>
/// <param name="ConfiguredModelId">Identifier of the configured model this pass runs on (its connection implied).</param>
/// <param name="Lens">
///     Optional specialist lens for this pass (e.g. <c>security</c>); <see langword="null" /> is an ordinary
///     resample pass. A lens pass runs a specialist prompt scoped to the files that lens targets.
/// </param>
/// <param name="Scope">
///     Optional scope for this pass; <see langword="null" /> is the per-file default and <c>pr_wide</c> runs the pass
///     at the job level rather than per file.
/// </param>
/// <param name="Shadow">Whether this pass runs in shadow mode. Additive metadata the runtime does not act on yet.</param>
/// <param name="ReasoningEffort">
///     Reasoning effort this pass asks the model to spend. <see cref="ReviewReasoningEffort.None" /> (default) sends no
///     effort level (current behavior); low/medium/high enable reasoning at the corresponding level.
/// </param>
public sealed record ReviewPassEntry(
    int Ordinal,
    Guid ConfiguredModelId,
    string? Lens = null,
    string? Scope = null,
    bool Shadow = false,
    ReviewReasoningEffort ReasoningEffort = ReviewReasoningEffort.None);

/// <summary>Crawl configuration response.</summary>
public sealed record CrawlConfigResponse(
    Guid Id,
    Guid ClientId,
    ScmProvider Provider,
    Guid? OrganizationScopeId,
    string ProviderScopePath,
    string ProviderProjectKey,
    int CrawlIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CrawlRepoFilterResponse>? RepoFilters = null,
    ProCursorSourceScopeMode ProCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
    IReadOnlyList<Guid>? ProCursorSourceIds = null,
    IReadOnlyList<Guid>? InvalidProCursorSourceIds = null,
    float? ReviewTemperature = null);

/// <summary>A single repo filter entry in a crawl config response.</summary>
public sealed record CrawlRepoFilterResponse(
    Guid Id,
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);

/// <summary>Request body for creating a client.</summary>
public sealed record CreateClientRequest(string DisplayName, [property: JsonRequired] Guid TenantId);

/// <summary>
///     Request body for patching a client. All fields are optional; omitted fields are left unchanged.
///     Set <see cref="CustomSystemMessage" /> to <c>""</c> (empty string) to clear an existing value.
/// </summary>
public sealed record PatchClientRequest(
    bool? IsActive = null,
    string? DisplayName = null,
    CommentResolutionBehavior? CommentResolutionBehavior = null,
    string? CustomSystemMessage = null,
    bool? ScmCommentPostingEnabled = null,
    bool? EnableEvidenceBackedVerification = null,
    bool? EnableLanguageRobustScreening = null,
    bool? EnableMultiPassUnion = null,
    bool? IncludeLinkedItemsInContext = null,
    IReadOnlyList<ReviewPassEntry>? ReviewPasses = null,
    ReviewReasoningEffort? BaselineReasoningEffort = null,
    BudgetConfigDto? BudgetConfig = null);
