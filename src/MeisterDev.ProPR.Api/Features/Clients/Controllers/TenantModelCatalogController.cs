// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

/// <summary>
///     A tenant's own catalog overrides. The case this exists for is a negotiated rate: a customer with a vendor
///     contract does not pay list price, and cost caps enforced against list price would misprice their spend.
///     Only pricing and the display name are overridable, because a model's capabilities are facts about the
///     model rather than about who is paying for it.
/// </summary>
[ApiController]
[Route("tenants/{tenantId:guid}/model-catalog/overrides")]
public sealed class TenantModelCatalogController(IModelCatalogRepository catalog) : ControllerBase
{
    /// <summary>
    ///     Lists the catalog as it applies to this tenant, so an administrator can choose a model to negotiate a
    ///     rate for rather than having to know its provider and identifier by heart.
    /// </summary>
    /// <param name="tenantId">Tenant whose catalog is browsed.</param>
    /// <param name="providerId">Restrict to a single catalog provider, or omit for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The effective catalog entries for this tenant.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this tenant.</response>
    [HttpGet("/tenants/{tenantId:guid}/model-catalog/models")]
    [ProducesResponseType(typeof(IReadOnlyList<AiModelCatalogEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetModels(
        Guid tenantId,
        [FromQuery] string? providerId = null,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        return this.Ok(await catalog.GetEffectiveForTenantAsync(tenantId, providerId, ct));
    }

    /// <summary>Lists the catalog providers available to browse.</summary>
    /// <param name="tenantId">Tenant whose catalog is browsed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The providers the catalog describes.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this tenant.</response>
    [HttpGet("/tenants/{tenantId:guid}/model-catalog/providers")]
    [ProducesResponseType(typeof(IReadOnlyList<ModelCatalogProviderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProviders(Guid tenantId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var providers = await catalog.GetProvidersAsync(ct);
        return this.Ok(
            providers
                .Select(provider => new ModelCatalogProviderResponse(provider.ProviderId, provider.ProviderName, provider.ModelCount))
                .ToList());
    }

    /// <summary>Lists the tenant's overrides.</summary>
    /// <param name="tenantId">Tenant whose overrides are listed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The tenant's overrides.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this tenant.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AiModelCatalogOverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOverrides(Guid tenantId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        return this.Ok(await catalog.GetTenantOverridesAsync(tenantId, ct));
    }

    /// <summary>
    ///     Records or replaces the tenant's override for one model. A price left empty is inherited rather than
    ///     treated as zero, so clearing every field returns the model to the snapshot's pricing.
    /// </summary>
    /// <param name="tenantId">Tenant the override belongs to.</param>
    /// <param name="request">The override to store.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The override was stored.</response>
    /// <response code="400">The request did not identify a provider and model.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this tenant.</response>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpsertOverride(
        Guid tenantId,
        [FromBody] AiModelCatalogOverrideDto request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.ProviderId)
            || string.IsNullOrWhiteSpace(request.RemoteModelId))
        {
            this.ModelState.AddModelError("providerId", "A provider id and a remote model id are required.");
            return this.ValidationProblem();
        }

        if (Negative(request.InputCostPer1MUsd)
            || Negative(request.OutputCostPer1MUsd)
            || Negative(request.CachedInputCostPer1MUsd)
            || Negative(request.CacheWriteCostPer1MUsd))
        {
            this.ModelState.AddModelError("cost", "A negotiated price cannot be negative.");
            return this.ValidationProblem();
        }

        await catalog.UpsertTenantOverrideAsync(tenantId, request, ct);
        return this.NoContent();
    }

    /// <summary>
    ///     Defines a model the catalog does not describe, so a private fine-tune, a release newer than the bundled
    ///     snapshot, or a self-hosted model becomes selectable and budgeted immediately.
    /// </summary>
    /// <param name="tenantId">Tenant the definition belongs to.</param>
    /// <param name="request">The model's own facts, since there is no snapshot entry to inherit them from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The model was defined.</response>
    /// <response code="400">The request was incomplete, priced negatively, or named a model the catalog already describes.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this tenant.</response>
    [HttpPut("/tenants/{tenantId:guid}/model-catalog/models")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DefineModel(
        Guid tenantId,
        [FromBody] AiModelCatalogDefinitionDto request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.ProviderId)
            || string.IsNullOrWhiteSpace(request.RemoteModelId))
        {
            this.ModelState.AddModelError("providerId", "A provider id and a remote model id are required.");
            return this.ValidationProblem();
        }

        if (Negative(request.InputCostPer1MUsd)
            || Negative(request.OutputCostPer1MUsd)
            || Negative(request.CachedInputCostPer1MUsd)
            || Negative(request.CacheWriteCostPer1MUsd))
        {
            this.ModelState.AddModelError("cost", "A price cannot be negative.");
            return this.ValidationProblem();
        }

        try
        {
            await catalog.UpsertTenantModelDefinitionAsync(tenantId, request, ct);
        }
        catch (ModelCatalogDefinitionConflictException exception)
        {
            // Not a server fault: the operator asked for the wrong instrument, and the message says which one
            // to use instead.
            this.ModelState.AddModelError("remoteModelId", exception.Message);
            return this.ValidationProblem();
        }

        return this.NoContent();
    }

    /// <summary>Removes the tenant's override for one model, returning it to the snapshot's values.</summary>
    /// <param name="tenantId">Tenant the override belongs to.</param>
    /// <param name="providerId">Catalog provider identifier.</param>
    /// <param name="remoteModelId">Model identifier as the provider knows it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">The override was removed.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this tenant.</response>
    /// <response code="404">No override existed for that model.</response>
    [HttpDelete("{providerId}/{remoteModelId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteOverride(
        Guid tenantId,
        string providerId,
        string remoteModelId,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        return await catalog.DeleteTenantOverrideAsync(tenantId, providerId, remoteModelId, ct)
            ? this.NoContent()
            : this.NotFound();
    }

    private static bool Negative(decimal? value) => value is < 0m;
}
