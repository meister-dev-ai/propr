// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

/// <summary>
///     Lists the scopes and repositories a client's provider connection can reach, so the mention
///     configuration form can offer them for selection.
/// </summary>
/// <remarks>
///     Takes a connection identifier where the Azure DevOps discovery endpoints take an organization scope.
///     An Azure DevOps mention configuration names an organization the client configured separately; on
///     GitHub, GitLab and Forgejo the host is the connection's own, so the connection identifies what to ask
///     and its host base URL is stored as the configuration's scope path.
///     Requires client administrator access, matching the mention configuration endpoints this feeds.
/// </remarks>
[ApiController]
[Route("admin/clients/{clientId:guid}/providers/{provider}/discovery")]
public sealed partial class ClientProviderDiscoveryController(
    IClientScmConnectionRepository connectionRepository,
    IScmProviderRegistry providerRegistry,
    ILogger<ClientProviderDiscoveryController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private const string UnknownConnectionMessage =
        "That connection does not belong to this client, is not enabled, or is not for the selected provider.";

    private const string DiscoveryRefusedMessage =
        "The provider refused the request. Check that the connection's token may list what was asked for.";

    /// <summary>
    ///     Returns 409 when the installation is not entitled to the mention-answering capability.
    /// </summary>
    /// <remarks>
    ///     Checked on every action here. The Azure DevOps discovery endpoints check their capability only when
    ///     the caller passes <c>purpose=crawl</c>, because those endpoints serve both crawl configuration and
    ///     mention configuration. This controller serves mention configuration only, and a check that depends
    ///     on an optional query parameter can be skipped by omitting it. If a second purpose is added here, add
    ///     the purpose parameter and select the capability from it.
    /// </remarks>
    private async Task<IActionResult?> RequireMentionAnsweringCapabilityAsync(CancellationToken ct)
    {
        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.MentionAnswering,
            ct);

        return capability is null ? null : new PremiumFeatureUnavailableResult(capability);
    }

    /// <summary>Lists the owners, organizations, or groups the connection can reach.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="provider">Provider family the connection belongs to.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The scopes the connection can reach.</response>
    /// <response code="400">The connection is unusable, or the provider refused the request.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is not an administrator of the client.</response>
    /// <response code="404">The provider family has no discovery in this deployment.</response>
    /// <response code="409">This installation is not entitled to answer mentions.</response>
    [HttpGet("scopes")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderScopeOptionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetScopes(
        Guid clientId,
        ScmProvider provider,
        [FromQuery] Guid connectionId,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireMentionAnsweringCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var host = await this.ResolveHostAsync(clientId, provider, connectionId, ct);
        if (host is null)
        {
            return this.BadRequest(new { error = UnknownConnectionMessage });
        }

        IRepositoryDiscoveryProvider discovery;
        try
        {
            discovery = providerRegistry.GetRepositoryDiscoveryProvider(provider);
        }
        catch (InvalidOperationException ex)
        {
            LogDiscoveryUnavailable(logger, provider, ex);
            return this.NotFound();
        }

        try
        {
            var scopes = await discovery.ListScopesAsync(clientId, host, ct);
            return this.Ok(scopes.Select(scope => new ProviderScopeOptionResponse(scope, scope)).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            // Reported as a refusal rather than answered with an empty list, which would read as a connection
            // that can reach nothing.
            LogDiscoveryRefused(logger, provider, clientId, ex);
            return this.BadRequest(new { error = DiscoveryRefusedMessage });
        }
    }

    /// <summary>Lists the repositories in one scope the connection can reach.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="provider">Provider family the connection belongs to.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="scopePath">The owner, organization, or group to list within.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The repositories in that scope.</response>
    /// <response code="400">The connection is unusable, the scope is missing, or the provider refused.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is not an administrator of the client.</response>
    /// <response code="404">The provider family has no discovery in this deployment.</response>
    /// <response code="409">This installation is not entitled to answer mentions.</response>
    [HttpGet("repositories")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderRepositoryOptionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetRepositories(
        Guid clientId,
        ScmProvider provider,
        [FromQuery] Guid connectionId,
        [FromQuery] string scopePath,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireMentionAnsweringCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        if (string.IsNullOrWhiteSpace(scopePath))
        {
            this.ModelState.AddModelError(nameof(scopePath), "scopePath is required.");
            return this.ValidationProblem();
        }

        var host = await this.ResolveHostAsync(clientId, provider, connectionId, ct);
        if (host is null)
        {
            return this.BadRequest(new { error = UnknownConnectionMessage });
        }

        IRepositoryDiscoveryProvider discovery;
        try
        {
            discovery = providerRegistry.GetRepositoryDiscoveryProvider(provider);
        }
        catch (InvalidOperationException ex)
        {
            LogDiscoveryUnavailable(logger, provider, ex);
            return this.NotFound();
        }

        try
        {
            var repositories = await discovery.ListRepositoriesAsync(clientId, host, scopePath.Trim(), ct);
            return this.Ok(
                repositories
                    .Select(repository => new ProviderRepositoryOptionResponse(
                        repository.ExternalRepositoryId,
                        repository.ProjectPath,
                        repository.OwnerOrNamespace))
                    .ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            LogDiscoveryRefused(logger, provider, clientId, ex);
            return this.BadRequest(new { error = DiscoveryRefusedMessage });
        }
    }

    /// <summary>
    ///     Resolves the host to ask, from a connection the client holds for the named provider.
    /// </summary>
    /// <remarks>
    ///     The provider in the route has to agree with the connection's own. A client holding two providers at
    ///     one host would otherwise be able to list one through the other's adapter.
    /// </remarks>
    private async Task<ProviderHostRef?> ResolveHostAsync(
        Guid clientId,
        ScmProvider provider,
        Guid connectionId,
        CancellationToken ct)
    {
        if (connectionId == Guid.Empty)
        {
            return null;
        }

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null || !connection.IsActive || connection.ProviderFamily != provider)
        {
            return null;
        }

        return new ProviderHostRef(provider, connection.HostBaseUrl);
    }
}

/// <summary>One owner, organization, or group a connection can reach.</summary>
/// <param name="ScopePath">What the provider is addressed by, and what a configuration stores.</param>
/// <param name="DisplayName">What to show an operator.</param>
public sealed record ProviderScopeOptionResponse(string ScopePath, string DisplayName);

/// <summary>One repository a connection can reach within a scope.</summary>
/// <param name="RepositoryId">The provider-native identifier, which survives a rename.</param>
/// <param name="DisplayName">The repository's path, for reading.</param>
/// <param name="ScopePath">The owner, organization, or group it belongs to.</param>
public sealed record ProviderRepositoryOptionResponse(string RepositoryId, string DisplayName, string ScopePath);
