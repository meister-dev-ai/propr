// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Per-client logical-model configuration: the client's override entries (which shadow the tenant catalog) and the
///     purpose → logical-model map. Requires the client-administrator role — the same authority as other client config.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/logical-models")]
[TenantData(TenantDataAccess.Metadata)]
public sealed class ClientLogicalModelsController(
    ILogicalModelCatalogRepository catalog,
    IClientRegistry clients,
    IAiConnectionRepository connections,
    IAiConnectionScopeGuard scopeGuard) : ControllerBase
{
    /// <summary>
    ///     The logical models effective for this client — the client's overrides plus the tenant-catalog entries an
    ///     override does not shadow — with authorized, nonsecret referenced model metadata for readiness checks.
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

        var tenantId = await clients.GetTenantIdAsync(clientId, ct);
        var referenced = tenantId is { } resolvedTenantId && resolvedTenantId != Guid.Empty
            ? await connections.GetByIdsAsync(effective.Select(e => e.ConnectionId).Distinct().ToArray(), ct)
            : [];
        var authorized = tenantId is { } scopedTenantId && scopedTenantId != Guid.Empty
            ? await scopeGuard.ValidateManyAsync(referenced, scopedTenantId, ct)
            : new HashSet<Guid>();
        var byId = referenced.ToDictionary(c => c.Id);
        for (var index = 0; index < effective.Count; index++)
        {
            var entry = effective[index];
            var metadata = new LogicalModelReferenceResponse("unavailable");
            if (tenantId is { } ownerTenantId && ownerTenantId != Guid.Empty)
            {
                byId.TryGetValue(entry.ConnectionId, out var connection);
                if (connection is not null &&
                    authorized.Contains(connection.Id) &&
                    connection.Availability.State == AiConnectionAvailabilityState.Available &&
                    connection.ConfiguredModels.FirstOrDefault(model => model.Id == entry.ConfiguredModelId) is { } model)
                {
                    metadata = new LogicalModelReferenceResponse(
                        "available", model.RemoteModelId, connection.DisplayName, model.OperationKinds,
                        model.SupportedProtocolModes, model.TokenizerName, model.MaxInputTokens,
                        model.EmbeddingDimensions, connection.IsActive, connection.Verification.Status,
                        model.ReasoningLevels, model.DefaultReasoningLevel);
                }
            }

            effective[index] = entry with { ReferencedModel = metadata };
        }

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
        var levels = await catalog.GetPurposeReasoningLevelsAsync(clientId, ct);
        return this.Ok(map.Select(kvp => new PurposeRoleResponse(kvp.Key, kvp.Value, levels.GetValueOrDefault(kvp.Key))).ToList());
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

        // The level of the assignment is checked against the levels of the model behind the logical model.
        var level = ReasoningLevelSelection.Normalize(request.ReasoningLevel);
        if (level is not null && await this.PurposeLevelRefusalAsync(clientId, request.LogicalModelName, level, ct) is { } refusal)
        {
            this.ModelState.AddModelError("reasoningLevel", refusal);
            return this.ValidationProblem();
        }

        await catalog.SetPurposeRoleAsync(clientId, purpose, request.LogicalModelName, level, ct);
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

    // The reason a purpose cannot run at the level on the logical model, or null. A logical model or a model this
    // client cannot resolve leaves the check to the level-name rule, because the purpose itself still resolves later.
    private async Task<string?> PurposeLevelRefusalAsync(Guid clientId, string logicalModelName, string level, CancellationToken ct)
    {
        // A client override shadows the tenant entry with the same name.
        var entry = (await catalog.GetClientOverridesAsync(clientId, ct))
            .Concat(await catalog.GetTenantEntriesForClientAsync(clientId, ct))
            .FirstOrDefault(candidate => string.Equals(candidate.Name, logicalModelName, StringComparison.OrdinalIgnoreCase));
        var model = entry is null
            ? null
            : (await connections.GetByIdAsync(entry.ConnectionId, ct))?.ConfiguredModels.FirstOrDefault(candidate => candidate.Id == entry.ConfiguredModelId);
        return ReasoningLevelSelection.Refusal(level, model?.ReasoningLevels, model?.RemoteModelId ?? logicalModelName);
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
    string? ReasoningLevel = null,
    string ProtocolMode = ProviderDeclaredProtocolModes.Auto)
{
    /// <summary>Maps to the application DTO, minting a fresh id (the repository assigns one when empty).</summary>
    public LogicalModelDto ToDto()
    {
        return new LogicalModelDto(
            Guid.Empty,
            this.Name,
            this.Capability,
            this.ConnectionId,
            this.ConfiguredModelId,
            ReasoningLevelSelection.Normalize(this.ReasoningLevel),
            this.ProtocolMode);
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
    string? ReasoningLevel,
    string ProtocolMode,
    string Scope)
{
    /// <summary>Authorized referenced model metadata; unavailable references omit all model and profile fields.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LogicalModelReferenceResponse? ReferencedModel { get; init; }

    /// <summary>Projects an application DTO into the response, tagging its scope (<c>client</c> or <c>tenant</c>).</summary>
    public static LogicalModelResponse From(LogicalModelDto dto, string scope)
    {
        return new LogicalModelResponse(dto.Id, dto.Name, dto.Capability, dto.ConnectionId, dto.ConfiguredModelId, dto.ReasoningLevel, dto.ProtocolMode, scope);
    }
}

/// <summary>Nonsecret model metadata for effective logical-model readiness.</summary>
public sealed record LogicalModelReferenceResponse(
    [property: System.ComponentModel.DataAnnotations.Required]
    string Availability,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RemoteModelId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ConnectionDisplayName = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AiOperationKind>? OperationKinds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? SupportedProtocolModes = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? TokenizerName = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? MaxInputTokens = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? EmbeddingDimensions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? IsActive = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    AiVerificationStatus? VerificationStatus = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ReasoningLevels = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? DefaultReasoningLevel = null);

/// <summary>Payload to map a purpose to a logical model.</summary>
/// <param name="LogicalModelName">The logical model the purpose runs on.</param>
/// <param name="ReasoningLevel">
///     The reasoning level the purpose runs at, by the provider's name, or null to leave it to the level of the logical
///     model and the default level of its configured model. A level the model does not accept is refused.
/// </param>
public sealed record SetPurposeRoleRequest(string LogicalModelName, string? ReasoningLevel = null);

/// <summary>One purpose → logical-model mapping row.</summary>
/// <param name="Purpose">The purpose.</param>
/// <param name="LogicalModelName">The logical model the purpose runs on.</param>
/// <param name="ReasoningLevel">The reasoning level the assignment sets, or null.</param>
public sealed record PurposeRoleResponse(AiPurpose Purpose, string LogicalModelName, string? ReasoningLevel = null);
