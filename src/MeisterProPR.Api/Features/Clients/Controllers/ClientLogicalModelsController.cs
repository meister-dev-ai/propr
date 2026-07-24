// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Per-client logical-model configuration: the client's override entries (which shadow the tenant catalog) and the
///     purpose → logical-model map. Requires the client-administrator role — the same authority as other client config.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/logical-models")]
public sealed class ClientLogicalModelsController(ILogicalModelCatalogRepository catalog) : ControllerBase
{
    /// <summary>
    ///     The logical models effective for this client — the client's overrides plus the tenant-catalog entries an
    ///     override does not shadow — for the pass and purpose editors' pickers.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LogicalModelResponse>), 200)]
    public async Task<IActionResult> ListEffective(Guid clientId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
        if (auth is not null)
        {
            return auth;
        }

        var overrides = await catalog.GetClientOverridesAsync(clientId, ct);
        var tenantEntries = await catalog.GetTenantEntriesForClientAsync(clientId, ct);
        var overrideNames = overrides.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        var effective = overrides.Select(entry => LogicalModelResponse.From(entry, "client"))
            .Concat(
                tenantEntries
                    .Where(entry => !overrideNames.Contains(entry.Name))
                    .Select(entry => LogicalModelResponse.From(entry, "tenant")))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        return this.Ok(effective);
    }

    /// <summary>The client's own override logical models (not the inherited tenant catalog).</summary>
    [HttpGet("overrides")]
    [ProducesResponseType(typeof(IReadOnlyList<LogicalModelResponse>), 200)]
    public async Task<IActionResult> ListOverrides(Guid clientId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
        if (auth is not null)
        {
            return auth;
        }

        var overrides = await catalog.GetClientOverridesAsync(clientId, ct);
        return this.Ok(overrides.Select(entry => LogicalModelResponse.From(entry, "client")).ToList());
    }

    /// <summary>Creates a per-client override logical model.</summary>
    [HttpPost("overrides")]
    [ProducesResponseType(typeof(LogicalModelResponse), 201)]
    public async Task<IActionResult> CreateOverride(Guid clientId, [FromBody] LogicalModelWriteRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
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
            await catalog.AddClientOverrideAsync(clientId, request.ToDto(), ct);
            return this.CreatedAtAction(nameof(this.ListOverrides), new { clientId }, LogicalModelResponse.From(request.ToDto(), "client"));
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

    /// <summary>Updates a per-client override's mapping (connection, model, reasoning, protocol). 404 if absent.</summary>
    [HttpPut("overrides/{name}")]
    public async Task<IActionResult> UpdateOverride(Guid clientId, string name, [FromBody] LogicalModelWriteRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
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
            return await catalog.UpdateClientOverrideAsync(clientId, name, request.ToDto(), ct)
                ? this.NoContent()
                : this.NotFound();
        }
        catch (LogicalModelReferenceInvalidException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Renames a per-client override. 404 if the old name is absent, 409 if the new name is taken.</summary>
    [HttpPost("overrides/{name}/rename")]
    public async Task<IActionResult> RenameOverride(Guid clientId, string name, [FromBody] RenameLogicalModelRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
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
            return await catalog.RenameClientOverrideAsync(clientId, name, request.NewName, ct)
                ? this.NoContent()
                : this.NotFound();
        }
        catch (DuplicateLogicalModelException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Deletes a per-client override. 404 if it does not exist.</summary>
    [HttpDelete("overrides/{name}")]
    public async Task<IActionResult> DeleteOverride(Guid clientId, string name, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
        if (auth is not null)
        {
            return auth;
        }

        return await catalog.DeleteClientOverrideAsync(clientId, name, ct) ? this.NoContent() : this.NotFound();
    }

    /// <summary>The client's purpose → logical-model map.</summary>
    [HttpGet("purposes")]
    [ProducesResponseType(typeof(IReadOnlyList<PurposeRoleResponse>), 200)]
    public async Task<IActionResult> ListPurposeRoles(Guid clientId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
        if (auth is not null)
        {
            return auth;
        }

        var map = await catalog.GetPurposeRolesAsync(clientId, ct);
        return this.Ok(map.Select(kvp => new PurposeRoleResponse(kvp.Key, kvp.Value)).ToList());
    }

    /// <summary>Maps an internal AI purpose to a logical model for this client.</summary>
    [HttpPut("purposes/{purpose}")]
    public async Task<IActionResult> SetPurposeRole(Guid clientId, AiPurpose purpose, [FromBody] SetPurposeRoleRequest request, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
        if (auth is not null)
        {
            return auth;
        }

        if (!Enum.IsDefined(purpose))
        {
            return this.NotFound();
        }

        var invalid = this.ValidateName(request.LogicalModelName);
        if (invalid is not null)
        {
            return invalid;
        }

        await catalog.SetPurposeRoleAsync(clientId, purpose, request.LogicalModelName, ct);
        return this.NoContent();
    }

    /// <summary>Removes a purpose mapping (the purpose then resolves through the client's AI purpose bindings again).</summary>
    [HttpDelete("purposes/{purpose}")]
    public async Task<IActionResult> RemovePurposeRole(Guid clientId, AiPurpose purpose, CancellationToken ct = default)
    {
        var auth = this.RequireClientAdmin(clientId);
        if (auth is not null)
        {
            return auth;
        }

        return await catalog.RemovePurposeRoleAsync(clientId, purpose, ct) ? this.NoContent() : this.NotFound();
    }

    private IActionResult? RequireClientAdmin(Guid clientId)
    {
        return AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
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

/// <summary>Create/update payload for a logical model (the name is the business key within its scope).</summary>
public sealed record LogicalModelWriteRequest(
    string Name,
    AiOperationKind Capability,
    Guid ConnectionId,
    Guid ConfiguredModelId,
    ReviewReasoningEffort ReasoningEffort = ReviewReasoningEffort.None,
    AiProtocolMode ProtocolMode = AiProtocolMode.Auto)
{
    /// <summary>Maps to the application DTO, minting a fresh id (the repository assigns one when empty).</summary>
    public LogicalModelDto ToDto()
    {
        return new LogicalModelDto(Guid.Empty, this.Name, this.Capability, this.ConnectionId, this.ConfiguredModelId, this.ReasoningEffort, this.ProtocolMode);
    }
}

/// <summary>Rename payload.</summary>
public sealed record RenameLogicalModelRequest(string NewName);

/// <summary>One logical model as returned to the client, tagged by the scope it came from (client override or tenant).</summary>
public sealed record LogicalModelResponse(
    Guid Id,
    string Name,
    AiOperationKind Capability,
    Guid ConnectionId,
    Guid ConfiguredModelId,
    ReviewReasoningEffort ReasoningEffort,
    AiProtocolMode ProtocolMode,
    string Scope)
{
    /// <summary>Projects an application DTO into the response, tagging its scope (<c>client</c> or <c>tenant</c>).</summary>
    public static LogicalModelResponse From(LogicalModelDto dto, string scope)
    {
        return new LogicalModelResponse(
            dto.Id, dto.Name, dto.Capability, dto.ConnectionId, dto.ConfiguredModelId, dto.ReasoningEffort, dto.ProtocolMode, scope);
    }
}

/// <summary>Payload to map a purpose to a logical model.</summary>
public sealed record SetPurposeRoleRequest(string LogicalModelName);

/// <summary>One purpose → logical-model mapping row.</summary>
public sealed record PurposeRoleResponse(AiPurpose Purpose, string LogicalModelName);
