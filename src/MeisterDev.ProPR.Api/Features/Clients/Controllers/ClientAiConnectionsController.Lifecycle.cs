// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Clients.Contracts;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

public sealed partial class ClientAiConnectionsController
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace AI purpose selections applied for client {ClientId}")]
    private static partial void LogWorkspacePurposesSelected(ILogger logger, Guid clientId);

    /// <summary>Verifies proposed client profile changes before atomically replacing the saved configuration.</summary>
    /// <param name="clientId">The owning client identifier.</param>
    /// <param name="connectionId">The client-owned connection identifier.</param>
    /// <param name="request">Proposed configuration. Blank credentials retain the protected saved values.</param>
    /// <param name="ct">Request cancellation.</param>
    /// <response code="200">The verified configuration was applied.</response>
    /// <response code="400">Configuration or provider verification failed; the saved profile is unchanged.</response>
    /// <response code="401">Authentication is required.</response>
    /// <response code="403">Client administrator access is required.</response>
    /// <response code="404">The client-owned profile does not exist.</response>
    /// <response code="409">The saved configuration changed during verification.</response>
    [HttpPost("{connectionId:guid}/verify-update")]
    [ProducesResponseType(typeof(AiConnectionDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> VerifyUpdateAiConnection(
        Guid clientId, Guid connectionId,
        [FromBody] UpdateAiConnectionRequest request, CancellationToken ct = default)
    {
        if (this.AuthorizeClientAccessAsync(clientId) is { } auth)
        {
            return auth;
        }

        var existing = await aiConnections.GetByIdAsync(connectionId, ct);
        if (existing is null || existing.ClientId != clientId || existing.TenantId is not null)
        {
            return this.NotFound();
        }

        var kind = request.ProviderKind ?? existing.ProviderKind;
        if (this.RefuseUnimplementedProvider(kind) is { } unimplemented)
        {
            return unimplemented;
        }

        if (await this.RefuseUnlicensedProviderAsync(kind, ct) is { } unlicensed)
        {
            return unlicensed;
        }

        if (await this.RefuseByTenantPolicyAsync(clientId, kind, request.BaseUrl ?? existing.BaseUrl, "verified", ct) is { } policyRefusal)
        {
            return policyRefusal;
        }

        var write = this.TryBuildWriteRequest(existing, request);
        if (write is null)
        {
            return this.ValidationProblem();
        }

        try
        {
            var result = await aiConnections.VerifyUpdateAsync(
                clientId, existing, write, async (candidate, cancellation) =>
                {
                    var driver = providerDrivers.GetRequired(candidate.ProviderKind);
                    try
                    {
                        return ProviderOutcomeGuard.Adopt(
                            await driver.VerifyAsync(candidate.ToProviderEndpoint(this.ProbeContext()), cancellation),
                            ProviderOutcomeGuard.SecretsOf(candidate));
                    }
                    catch (Exception) when (!cancellation.IsCancellationRequested)
                    {
                        return new AiVerificationResultDto(AiVerificationStatus.Failed, Summary: "Provider verification failed.");
                    }
                }, ct, this.HttpContext.Items[TenantMachineOperationPolicy.TenantItemKey] as Guid?);
            if (result.NotFound)
            {
                return this.NotFound();
            }

            if (result.Conflict)
            {
                return this.Conflict(new { error = "AI configuration changed during verification. Reload the connection and retry." });
            }

            if (!result.Applied)
            {
                return this.BadRequest(new { error = "Provider verification failed. Check the connection credentials and model settings, then retry." });
            }

            LogConnectionUpdated(logger, connectionId, clientId);
            return this.Ok(result.Connection);
        }
        catch (ProviderKindNotPermittedException)
        {
            return this.BadRequest(new { error = "The tenant no longer permits this provider or endpoint. Select a permitted provider." });
        }
        catch (ProviderRepointNotClearedException)
        {
            return this.BadRequest(new { error = "Enter credentials and settings for the selected provider before changing the provider family." });
        }
        catch (ProviderDeclaredValueRefusedException)
        {
            return this.BadRequest(new { error = "Correct the provider settings and endpoint policy before updating the connection." });
        }
        catch (LogicalModelReferenceInUseException)
        {
            return this.Conflict(
                new { error = "A logical model still references a removed model. Keep that model or update its logical reference in the cell console." });
        }
        catch (LogicalModelReferenceInvalidException)
        {
            return this.Conflict(
                new
                {
                    error =
                        "A logical model requires the current model capability. Keep compatible model settings or update the logical reference in the cell console."
                });
        }
    }

    /// <summary>Applies the Default, High and Embedding workspace purposes together using verified client-owned models.</summary>
    /// <param name="clientId">The owning client identifier.</param>
    /// <param name="request">All three configured model and connection selections.</param>
    /// <param name="ct">Request cancellation.</param>
    /// <response code="200">All selected purposes were applied together.</response>
    /// <response code="400">A selection is invalid or unverified; existing routing is unchanged.</response>
    /// <response code="401">Authentication is required.</response>
    /// <response code="403">Client administrator access is required.</response>
    /// <response code="409">A logical or managed binding conflicts, or configuration changed concurrently.</response>
    [HttpPost("select-purposes")]
    [ProducesResponseType(typeof(AiConfigurationResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(typeof(AiConfigurationResult), 409)]
    public async Task<IActionResult> SelectAiPurposes(
        Guid clientId, [FromBody] AiWorkspacePurposeSelection request,
        CancellationToken ct = default)
    {
        if (this.AuthorizeClientAccessAsync(clientId) is { } auth)
        {
            return auth;
        }

        if (request.Default is not null && request.High is not null && request.Embedding is not null)
        {
            var ids = new[] { request.Default.ConnectionId, request.High.ConnectionId, request.Embedding.ConnectionId }.Distinct().ToArray();
            foreach (var profile in await aiConnections.GetByIdsAsync(ids, ct))
            {
                if (profile.ClientId != clientId || profile.TenantId is not null)
                {
                    return this.BadRequest(new { error = "Select models from connections owned by this client." });
                }

                if (await this.RefuseUnlicensedProviderAsync(profile.ProviderKind, ct) is { } unlicensed)
                {
                    return unlicensed;
                }
            }
        }

        var result = await aiConnections.SelectPurposesAsync(
            clientId, request, ct,
            this.HttpContext.Items[TenantMachineOperationPolicy.TenantItemKey] as Guid?);
        if (result.Applied)
        {
            LogWorkspacePurposesSelected(logger, clientId);
            return this.Ok(result);
        }

        return result.Conflict ? this.Conflict(result) : this.BadRequest(result);
    }
}
