// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

/// <summary>
///     Read-only model catalog for one client, so an operator can browse and pick a model instead of typing its
///     identifier. Entries are returned with the client's tenant overrides already applied, and each carries the
///     layer its price came from so a negotiated rate is visible as such.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/model-catalog")]
public sealed class ClientModelCatalogController(IModelCatalogRepository catalog) : ControllerBase
{
    /// <summary>Lists the catalog providers available to pick from.</summary>
    /// <param name="clientId">Client whose catalog is browsed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The providers the catalog describes.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this client.</response>
    [HttpGet("providers")]
    [ProducesResponseType(typeof(IReadOnlyList<ModelCatalogProviderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProviders(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
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

    /// <summary>Lists the catalog models available to the client, with tenant overrides applied.</summary>
    /// <param name="clientId">Client whose catalog is browsed.</param>
    /// <param name="providerId">Restrict to a single catalog provider, or omit for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The effective catalog entries for this client.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not administer this client.</response>
    [HttpGet("models")]
    [ProducesResponseType(typeof(IReadOnlyList<AiModelCatalogEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetModels(
        Guid clientId,
        [FromQuery] string? providerId = null,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        return this.Ok(await catalog.GetEffectiveForClientAsync(clientId, providerId, ct));
    }
}

/// <summary>One provider the catalog describes.</summary>
/// <param name="ProviderId">Catalog provider identifier.</param>
/// <param name="ProviderName">Human-readable provider name.</param>
/// <param name="ModelCount">How many models the catalog lists for it.</param>
public sealed record ModelCatalogProviderResponse(string ProviderId, string ProviderName, int ModelCount);
