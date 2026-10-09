// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

/// <summary>Provides connection-scoped native discovery for admin configuration.</summary>
[ApiController]
[Route("admin/clients/{clientId:guid}/connections/{connectionId:guid}/discovery")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
[ProducesResponseType(StatusCodes.Status501NotImplemented)]
public sealed partial class ClientProviderDiscoveryController(
    IReviewConfigurationSelectionService selections,
    ILicensingCapabilityService? licensingCapabilityService = null,
    ILogger<ClientProviderDiscoveryController>? logger = null) : ControllerBase
{
    private async Task<IActionResult> ExecuteAsync(
        Guid clientId, Guid connectionId, string? purpose,
        Func<ConnectionDiscoveryContext, Task<object>> operation, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerMemberName]
        string operationName = "")
    {
        var normalizedPurpose = purpose?.Trim().ToLowerInvariant();
        if (normalizedPurpose is not ("crawl" or "mention" or "webhook" or "procursor"))
        {
            return this.BadRequest(new { error = "A supported discovery purpose is required." });
        }

        var auth = AuthHelpers.RequireClientRole(
            this.HttpContext, clientId,
            normalizedPurpose == "mention" ? ClientRole.ClientAdministrator : ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        var capabilityKey = normalizedPurpose switch
        {
            "crawl" => PremiumCapabilityKey.CrawlConfigs,
            "mention" => PremiumCapabilityKey.MentionAnswering,
            _ => null,
        };
        if (capabilityKey is not null)
        {
            var unavailable = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(licensingCapabilityService, capabilityKey, ct);
            if (unavailable is not null)
            {
                return new PremiumFeatureUnavailableResult(unavailable);
            }
        }

        try
        {
            var context = await selections.GetConnectionContextAsync(clientId, connectionId, ct);
            if (normalizedPurpose == "mention" && !selections.SupportsMentionConfiguration(context))
            {
                throw new NotSupportedException();
            }

            return this.Ok(await operation(context));
        }
        catch (NotSupportedException)
        {
            this.LogRefusal(clientId, connectionId, normalizedPurpose, operationName, "unsupported");
            return this.StatusCode(
                StatusCodes.Status501NotImplemented, new { error = "This discovery operation is not supported by the selected connection." });
        }
        catch (KeyNotFoundException)
        {
            this.LogRefusal(clientId, connectionId, normalizedPurpose, operationName, "not_found");
            return this.NotFound(new { error = "The selected discovery resource is not available." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogRefusal(clientId, connectionId, normalizedPurpose, operationName, "rejected");
            return this.BadRequest(new { error = "The selected connection or discovery selection could not be used. Check its configuration and access." });
        }
    }

    private void LogRefusal(Guid clientId, Guid connectionId, string purpose, string operation, string outcome)
    {
        if (logger is not null)
        {
            LogDiscoveryRefused(logger, clientId, connectionId, purpose, operation, outcome);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Discovery for client {ClientId} connection {ConnectionId} purpose {Purpose} operation {Operation} returned {Outcome}")]
    private static partial void LogDiscoveryRefused(ILogger logger, Guid clientId, Guid connectionId, string purpose, string operation, string outcome);

    /// <summary>Returns native labels, hierarchy and supported operations.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose: crawl, mention, webhook or procursor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The native discovery descriptor.</response>
    /// <response code="400">Invalid purpose or unavailable connection.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">The operation is unsupported.</response>
    [HttpGet("descriptor")]
    [ProducesResponseType(typeof(ConnectionDiscoveryDescriptor), StatusCodes.Status200OK)]
    public Task<IActionResult> GetDescriptor(Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, CancellationToken ct = default) =>
        this.ExecuteAsync(clientId, connectionId, purpose, context => Task.FromResult<object>(selections.GetDescriptor(context)), ct);

    /// <summary>Lists scopes reachable through the selected connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Scopes, including an empty successful listing.</response>
    /// <response code="400">Invalid selection or provider request failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">The operation is unsupported.</response>
    [HttpGet("scopes")]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectionDiscoveryScope>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetScopes(Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, CancellationToken ct = default) =>
        this.ExecuteAsync(clientId, connectionId, purpose, async context => await selections.GetScopesAsync(context, ct), ct);

    /// <summary>Lists projects when the native hierarchy has a project stage.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose.</param>
    /// <param name="scopeKey">Native scope key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Native projects.</response>
    /// <response code="400">Invalid selection or provider request failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">The connection has no project stage.</response>
    [HttpGet("projects")]
    [ProducesResponseType(typeof(IReadOnlyList<ScmDiscoveryProjectOption>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetProjects(
        Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, [FromQuery] string scopeKey, CancellationToken ct = default) =>
        this.ExecuteAsync(clientId, connectionId, purpose, async context => await selections.GetProjectsAsync(context, scopeKey, ct), ct);

    /// <summary>Lists sources with native persistent coordinates and identities.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose.</param>
    /// <param name="scopeKey">Native scope key.</param>
    /// <param name="projectId">Native project key when the hierarchy declares that stage.</param>
    /// <param name="sourceKind">Declared source kind.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Native source selections.</response>
    /// <response code="400">Invalid selection or provider request failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">The source kind is unsupported.</response>
    [HttpGet("sources")]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectionDiscoverySource>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetSources(
        Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, [FromQuery] string scopeKey,
        [FromQuery] string? projectId, [FromQuery, BindRequired] ProCursorSourceKind sourceKind, CancellationToken ct = default) =>
        this.ExecuteAsync(clientId, connectionId, purpose, async context => await selections.GetSourcesAsync(context, scopeKey, projectId, sourceKind, ct), ct);

    /// <summary>Lists branches when native branch discovery is supported.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose.</param>
    /// <param name="scopeKey">Native scope key.</param>
    /// <param name="projectId">Native project key.</param>
    /// <param name="sourceKind">Declared source kind.</param>
    /// <param name="canonicalSourceProvider">Canonical provider key returned by source discovery.</param>
    /// <param name="canonicalSourceValue">Canonical value returned by source discovery.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Native branch suggestions.</response>
    /// <response code="400">Invalid selection or provider request failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">Branch discovery is unsupported.</response>
    [HttpGet("branches")]
    [ProducesResponseType(typeof(IReadOnlyList<ScmDiscoveryBranchOption>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetBranches(
        Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, [FromQuery] string scopeKey,
        [FromQuery] string projectId, [FromQuery, BindRequired] ProCursorSourceKind sourceKind,
        [FromQuery] string canonicalSourceProvider, [FromQuery] string canonicalSourceValue, CancellationToken ct = default) =>
        this.ExecuteAsync(
            clientId, connectionId, purpose,
            async context => await selections.GetBranchesAsync(
                context, scopeKey, projectId, sourceKind, new(canonicalSourceProvider, canonicalSourceValue), ct), ct);

    /// <summary>Projects repository sources into filter options without requiring branch discovery.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose.</param>
    /// <param name="scopeKey">Native scope key.</param>
    /// <param name="projectId">Optional native project key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Filter options with known default branch suggestions.</response>
    /// <response code="400">Invalid selection or provider request failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">The operation is unsupported.</response>
    [HttpGet("filters")]
    [ProducesResponseType(typeof(IReadOnlyList<ScmDiscoveryCrawlFilterOption>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetFilters(
        Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, [FromQuery] string scopeKey, [FromQuery] string? projectId,
        CancellationToken ct = default) =>
        this.ExecuteAsync(
            clientId, connectionId, purpose, async context =>
                (await selections.GetSourcesAsync(context, scopeKey, projectId, ProCursorSourceKind.Repository, ct))
                .Select(source => new ScmDiscoveryCrawlFilterOption(
                    source.CanonicalSourceRef, source.DisplayName,
                    string.IsNullOrWhiteSpace(source.DefaultBranch) ? [] : [new(source.DefaultBranch, true)])).ToList(), ct);

    /// <summary>Resolves native configuration coordinates for the selected hierarchy.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Selected connection identifier.</param>
    /// <param name="purpose">Required configuration purpose.</param>
    /// <param name="scopeKey">Native scope key.</param>
    /// <param name="projectId">Optional native project key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Native persistent selection coordinates.</response>
    /// <response code="400">Invalid selection or provider request failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Insufficient client role.</response>
    /// <response code="404">The selected resource is not available.</response>
    /// <response code="409">Required licensed capability is unavailable.</response>
    /// <response code="501">The operation is unsupported.</response>
    [HttpGet("selection")]
    [ProducesResponseType(typeof(ConnectionDiscoverySelection), StatusCodes.Status200OK)]
    public Task<IActionResult> GetSelection(
        Guid clientId, Guid connectionId, [FromQuery, BindRequired] string? purpose, [FromQuery] string scopeKey,
        [FromQuery] string? projectId, CancellationToken ct = default) =>
        this.ExecuteAsync(
            clientId, connectionId, purpose, async _ =>
            {
                var selected = await selections.ResolveConnectionSelectionAsync(clientId, connectionId, scopeKey, projectId, null, null, null, ct);
                return new ConnectionDiscoverySelection(
                    selected.Provider, connectionId, scopeKey,
                    selected.OrganizationScopeId, selected.ProviderScopePath, selected.ProviderProjectKey);
            }, ct);
}
