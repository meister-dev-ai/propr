// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Tenant-catalog logical-model management: the tenant-wide entries a tenant's clients inherit. Requires the
///     tenant-administrator role. The system tenant has no tenant-catalog layer (writes are rejected 400).
/// </summary>
[ApiController]
[Route("tenants/{tenantId:guid}/logical-models")]
public sealed class TenantLogicalModelsController(ILogicalModelCatalogRepository catalog) : ControllerBase
{
    /// <summary>The tenant-catalog logical models for this tenant.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LogicalModelResponse>), 200)]
    public async Task<IActionResult> List(Guid tenantId, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var entries = await catalog.GetTenantEntriesAsync(tenantId, ct);
        return this.Ok(entries.Select(entry => LogicalModelResponse.From(entry, "tenant")).ToList());
    }

    /// <summary>Creates a tenant-catalog logical model. 400 for the system tenant, 409 for a duplicate name.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(LogicalModelResponse), 201)]
    public async Task<IActionResult> Create(Guid tenantId, [FromBody] LogicalModelWriteRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var invalid = this.ValidateName(request.Name);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            await catalog.AddTenantEntryAsync(tenantId, request.ToDto(), ct);
            return this.CreatedAtAction(nameof(this.List), new { tenantId }, LogicalModelResponse.From(request.ToDto(), "tenant"));
        }
        catch (SystemTenantLogicalModelCatalogException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (DuplicateLogicalModelException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (LogicalModelReferenceInvalidException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Renames a tenant-catalog entry. 404 if the old name is absent, 409 if the new name is taken.</summary>
    [HttpPost("{name}/rename")]
    public async Task<IActionResult> Rename(Guid tenantId, string name, [FromBody] RenameLogicalModelRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var invalid = this.ValidateName(request.NewName);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            return await catalog.RenameTenantEntryAsync(tenantId, name, request.NewName, ct)
                ? this.NoContent()
                : this.NotFound();
        }
        catch (DuplicateLogicalModelException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Updates a tenant-catalog entry's mapping (connection, model, reasoning, protocol). 404 if absent.</summary>
    [HttpPut("{name}")]
    public async Task<IActionResult> Update(Guid tenantId, string name, [FromBody] LogicalModelWriteRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        var invalid = this.ValidateName(request.Name);
        if (invalid is not null)
        {
            return invalid;
        }

        // The route name is the immutable key; a body carrying a different name would be silently ignored, so
        // reject the mismatch rather than accept an ambiguous request.
        if (!string.Equals(request.Name, name, StringComparison.Ordinal))
        {
            this.ModelState.AddModelError(nameof(request.Name), "The logical-model name in the body must match the name in the route.");
            return this.ValidationProblem();
        }

        try
        {
            return await catalog.UpdateTenantEntryAsync(tenantId, name, request.ToDto(), ct)
                ? this.NoContent()
                : this.NotFound();
        }
        catch (LogicalModelReferenceInvalidException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Deletes a tenant-catalog entry. 404 if it does not exist.</summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(Guid tenantId, string name, CancellationToken ct = default)
    {
        var auth = this.RequireTenantAdmin(tenantId);
        if (auth is not null)
        {
            return auth;
        }

        return await catalog.DeleteTenantEntryAsync(tenantId, name, ct) ? this.NoContent() : this.NotFound();
    }

    private IActionResult? RequireTenantAdmin(Guid tenantId)
    {
        return AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
    }

    private IActionResult? ValidateName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 100)
        {
            return null;
        }

        this.ModelState.AddModelError("name", "A logical-model name is required and must be at most 100 characters.");
        return this.ValidationProblem();
    }
}
