// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using FluentValidation;
using FluentValidation.Results;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Administrative tenant endpoints for platform and tenant administrators.</summary>
[ApiController]
[Route("admin/tenants")]
public sealed class TenantsController(
    ITenantAdminService tenantAdminService,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    /// <summary>Lists tenants visible to the current caller.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListTenants(CancellationToken ct)
    {
        if (AuthHelpers.IsAdmin(this.HttpContext))
        {
            return this.Ok(await tenantAdminService.GetAllAsync(ct));
        }

        var auth = AuthHelpers.RequireAnyTenantRole(this.HttpContext, TenantRole.TenantUser);
        if (auth is not null)
        {
            return auth;
        }

        var visibleTenants = new List<TenantDto>();
        foreach (var tenantId in AuthHelpers.GetTenantRoles(this.HttpContext).Keys)
        {
            var tenant = await tenantAdminService.GetByIdAsync(tenantId, ct);
            if (tenant is not null)
            {
                visibleTenants.Add(tenant);
            }
        }

        return this.Ok(visibleTenants);
    }

    /// <summary>Returns one tenant when the caller belongs to it or is a platform administrator.</summary>
    [HttpGet("{tenantId:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTenant(Guid tenantId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantUser);
        if (auth is not null)
        {
            return auth;
        }

        var tenant = await tenantAdminService.GetByIdAsync(tenantId, ct);
        return tenant is null ? this.NotFound() : this.Ok(tenant);
    }

    /// <summary>Creates a new tenant boundary.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTenant(
        [FromBody] CreateTenantRequest request,
        [FromServices] IValidator<CreateTenantRequest> validator,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var existing = await tenantAdminService.GetBySlugAsync(request.Slug, ct);
        if (existing is not null)
        {
            return this.Conflict(new { error = "A tenant with that slug already exists." });
        }

        try
        {
            var created = await tenantAdminService.CreateAsync(request.Slug, request.DisplayName, ct: ct);
            return this.CreatedAtAction(nameof(this.GetTenant), new { tenantId = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Applies partial tenant policy updates.</summary>
    [HttpPatch("{tenantId:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchTenant(
        Guid tenantId,
        [FromBody] UpdateTenantRequest request,
        [FromServices] IValidator<UpdateTenantRequest> validator,
        [FromServices] IAiProviderDriverRegistry providerDrivers,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (this.RefuseUnclaimedProviders(request.AllowedAiProviderKinds, providerDrivers) is { } unclaimed)
        {
            return unclaimed;
        }

        try
        {
            if (request.Budget is not null || request.ReviewLimits is not null)
            {
                // Budgeting is a licensed capability, and it covers the tenant's caps and its per-file limits
                // alike: both bound what the tenant's reviews may spend, so setting either requires the
                // capability to be enabled for the installation. Values already stored keep being enforced in
                // every edition. The lookup sits inside the same try as the update so a failure to resolve the
                // capability returns the endpoint's error result instead of an unstructured 500.
                var unavailableCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
                    licensingCapabilityService,
                    PremiumCapabilityKey.Budgeting,
                    ct);
                if (unavailableCapability is not null)
                {
                    // The capability's own message describes budgets, and a request that carried only the
                    // per-file limits would be refused with a message naming something it did not send. The
                    // message therefore names the settings of this request, and the capability's message
                    // follows it with why the capability is unavailable.
                    return new PremiumFeatureUnavailableResult(
                        unavailableCapability,
                        $"Setting {DescribeRefusedSettings(request)} requires the budgeting capability. {unavailableCapability.Message}".TrimEnd());
                }
            }

            var updated = await tenantAdminService.PatchAsync(
                tenantId,
                request.DisplayName,
                request.IsActive,
                request.LocalLoginEnabled,
                request.AllowedAiProviderKinds,
                request.AllowedAiEndpointHosts,
                request.RemovedUnresolvedAiProviderKinds,
                request.ReasoningCapturePolicy,
                request.Budget,
                request.ReviewLimits,
                ct);

            return updated is null ? this.NotFound() : this.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    // A permitted-family entry names a family by its identity key, and an entry no loaded family claims permits
    // nothing. Refused here, naming the entry and what is available, so a mistyped key is corrected on the form
    // rather than stored and then refusing every provider the tenant has. An entry already stored that stopped
    // resolving is a different case: it stays on the policy, is reported on the tenant, and is removed by naming
    // it for removal.
    private IActionResult? RefuseUnclaimedProviders(
        IReadOnlyList<string>? requested,
        IAiProviderDriverRegistry providerDrivers)
    {
        if (requested is not { Count: > 0 })
        {
            return null;
        }

        var unclaimed = requested
            .Where(entry => !providerDrivers.IsRegistered(entry))
            .ToList();

        if (unclaimed.Count == 0)
        {
            return null;
        }

        this.ModelState.AddModelError(
            "allowedAiProviderKinds",
            $"No installed provider family claims {string.Join(", ", unclaimed.Select(entry => $"'{entry}'"))} "
            + $"(available: {string.Join(", ", providerDrivers.RegisteredKinds)}).");
        return this.ValidationProblem();
    }

    /// <summary>Names the settings of this request that the budgeting capability covers.</summary>
    /// <param name="request">The patch that was refused.</param>
    private static string DescribeRefusedSettings(UpdateTenantRequest request)
    {
        if (request.Budget is null)
        {
            return "this tenant's per-file review limits";
        }

        return request.ReviewLimits is null
            ? "this tenant's monthly spend caps"
            : "this tenant's monthly spend caps and its per-file review limits";
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
}

/// <summary>Create-tenant request payload.</summary>
public sealed record CreateTenantRequest(string Slug, string DisplayName);

/// <summary>Patch-tenant request payload.</summary>
/// <param name="DisplayName">New display name, or null to leave unchanged.</param>
/// <param name="IsActive">New active state, or null to leave unchanged.</param>
/// <param name="LocalLoginEnabled">New local-login policy, or null to leave unchanged.</param>
/// <param name="AllowedAiProviderKinds">
///     Provider families this tenant's clients may use, by identity key, or null to leave unchanged. An empty
///     list clears the restriction back to unrestricted. An entry no loaded family claims is refused, so a
///     mistyped key is reported on the form instead of leaving the tenant permitting nothing.
/// </param>
/// <param name="AllowedAiEndpointHosts">
///     Endpoint hosts this tenant's clients may reach, or null to leave unchanged. An empty list clears the
///     restriction. An entry matches a host exactly, or any subdomain when written with a leading dot.
/// </param>
/// <param name="RemovedUnresolvedAiProviderKinds">
///     Permitted-family entries no loaded family claims, to remove from the policy. They are reported on the
///     tenant as <c>unresolvedAiProviderKinds</c>, and <paramref name="AllowedAiProviderKinds" /> carries only
///     entries a loaded family claims, so naming one here is how it is removed. Anything not named here survives
///     the write, so a removal cannot happen as a side effect of saving the families. An entry the tenant does
///     not hold is ignored.
/// </param>
/// <param name="ReasoningCapturePolicy">
///     Whether this tenant's review jobs capture model reasoning into the protocol, or null to leave it
///     unchanged. <see cref="Domain.Enums.ReasoningCapturePolicy.InstallationDefault" /> clears the tenant's
///     override and hands the decision back to the installation switch.
/// </param>
/// <param name="Budget">
///     Monthly USD budget caps for the tenant, or null to leave both caps unchanged. A cap set to null inside the
///     value clears that cap back to no limit.
/// </param>
/// <param name="ReviewLimits">
///     Per-file byte limits for this tenant's reviews, or null to leave both unchanged. A value set to null
///     inside the record puts the installation value back in force. The limits bound how much of one file the
///     reviewer reads, so they bound what a review spends: setting them requires the budgeting capability, as
///     <paramref name="Budget" /> does. Limits already stored are applied in every edition.
/// </param>
public sealed record UpdateTenantRequest(
    string? DisplayName,
    bool? IsActive,
    bool? LocalLoginEnabled,
    IReadOnlyList<string>? AllowedAiProviderKinds = null,
    IReadOnlyList<string>? AllowedAiEndpointHosts = null,
    IReadOnlyList<string>? RemovedUnresolvedAiProviderKinds = null,
    ReasoningCapturePolicy? ReasoningCapturePolicy = null,
    TenantBudgetConfigDto? Budget = null,
    TenantReviewLimitsDto? ReviewLimits = null);
