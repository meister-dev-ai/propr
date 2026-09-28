// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;

/// <summary>Manages repository review targets within one client.</summary>
[ApiController]
[Route("clients/{clientId:guid}/review-targets")]
public sealed class ClientReviewTargetsController(
    ICrawlConfigurationRepository configurations,
    IClientScmConnectionRepository connections,
    ILicensingCapabilityService? licensingCapabilityService = null,
    IScmProviderRegistry? providerRegistry = null) : ControllerBase
{
    /// <summary>Lists repositories reachable through a verified client connection.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="connectionId">Verified provider connection identifier.</param>
    /// <param name="scopePath">Provider scope to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Repositories returned by the provider.</response>
    /// <response code="400">The connection or scope is invalid, or the provider refused discovery.</response>
    /// <response code="403">The caller cannot administer this client.</response>
    /// <response code="404">Repository discovery is unavailable for the provider.</response>
    /// <response code="409">The crawl configuration capability is unavailable.</response>
    [HttpGet("repositories")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientReviewTargetRepositoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetRepositories(
        Guid clientId,
        [FromQuery] Guid connectionId,
        [FromQuery] string? scopePath,
        CancellationToken ct = default)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (denied is not null)
        {
            return denied;
        }

        var unavailable = await this.RequireCapabilityAsync(ct);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var connection = await connections.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null || !connection.IsActive ||
            !string.Equals(connection.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase) ||
            !TryGetOrigin(connection.HostBaseUrl, out var connectionOrigin))
        {
            return this.BadRequest(new { error = "The connection must belong to this client and be active and verified." });
        }

        var selectedScope = scopePath?.Trim();
        if (string.IsNullOrWhiteSpace(selectedScope) ||
            (connection.ProviderFamily == ScmProvider.AzureDevOps &&
             (!TryGetOrigin(selectedScope, out var selectedOrigin) ||
              !string.Equals(connectionOrigin, selectedOrigin, StringComparison.OrdinalIgnoreCase))) ||
            (connection.ProviderFamily != ScmProvider.AzureDevOps &&
             (Uri.TryCreate(selectedScope, UriKind.Absolute, out _) || selectedScope.StartsWith('/'))))
        {
            return this.BadRequest(new { error = "The provider scope is invalid for this connection." });
        }

        if (providerRegistry is null)
        {
            return this.NotFound();
        }

        IRepositoryDiscoveryProvider discovery;
        try
        {
            discovery = providerRegistry.GetRepositoryDiscoveryProvider(connection.ProviderFamily);
        }
        catch (InvalidOperationException)
        {
            return this.NotFound();
        }

        try
        {
            var host = new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl);
            var repositories = await discovery.ListRepositoriesAsync(clientId, host, selectedScope!, ct);
            return this.Ok(
                repositories.Select(repository =>
                    ToRepositoryResponse(repository)).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return this.BadRequest(new { error = "The provider refused repository discovery." });
        }
    }

    /// <summary>Lists configured repository review targets for the client.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The configured review targets.</response>
    /// <response code="403">The caller cannot access this client.</response>
    /// <response code="409">The crawl configuration capability is unavailable.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ClientReviewTargetResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetTargets(Guid clientId, CancellationToken ct = default)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (denied is not null)
        {
            return denied;
        }

        var unavailable = await this.RequireCapabilityAsync(ct);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var targets = (await configurations.GetByClientAsync(clientId, ct))
            .Select(ToTarget)
            .Where(target => target is not null)
            .Cast<ClientReviewTargetResponse>()
            .ToList();
        return this.Ok(targets);
    }

    /// <summary>Lists open pull requests for one configured repository review target.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="targetId">Configured repository review target identifier.</param>
    /// <param name="connectionId">Active, verified provider connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Up to 100 open pull requests from the provider.</response>
    /// <response code="400">The connection is invalid or does not cover the target.</response>
    /// <response code="403">The caller cannot access this client.</response>
    /// <response code="404">The target or provider adapter is unavailable.</response>
    /// <response code="409">The crawl configuration capability is unavailable.</response>
    /// <response code="502">The provider could not list pull requests.</response>
    [HttpGet("{targetId:guid}/open-reviews")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientOpenReviewResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetOpenReviews(
        Guid clientId,
        Guid targetId,
        [FromQuery] Guid connectionId,
        CancellationToken ct = default)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (denied is not null)
        {
            return denied;
        }

        var unavailable = await this.RequireCapabilityAsync(ct);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var target = (await configurations.GetByClientAsync(clientId, ct))
            .FirstOrDefault(configuration => configuration.Id == targetId);
        if (target is null || ToTarget(target) is not { } repositoryTarget)
        {
            return this.NotFound();
        }

        var connection = await connections.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null || !connection.IsActive ||
            !string.Equals(connection.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase) ||
            connection.ProviderFamily != target.Provider ||
            !TryGetOrigin(connection.HostBaseUrl, out var connectionOrigin) ||
            !TryGetOrigin(target.ProviderScopePath, out var targetOrigin) ||
            !string.Equals(connectionOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase) ||
            (target.Provider != ScmProvider.AzureDevOps &&
             !MatchesBaseUrl(connection.HostBaseUrl, target.ProviderScopePath)))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
        }

        if (providerRegistry is null)
        {
            return this.NotFound();
        }

        IReviewDiscoveryProvider discovery;
        try
        {
            discovery = providerRegistry.GetReviewDiscoveryProvider(target.Provider);
        }
        catch (InvalidOperationException)
        {
            return this.NotFound();
        }

        try
        {
            var host = new ProviderHostRef(target.Provider, connection.HostBaseUrl);
            var repository = new RepositoryRef(
                host,
                repositoryTarget.RepositoryId,
                target.ProviderProjectKey,
                target.Provider == ScmProvider.AzureDevOps
                    ? target.ProviderProjectKey
                    : $"{target.ProviderProjectKey.TrimEnd('/')}/{repositoryTarget.RepositoryName}",
                repositoryTarget.RepositoryName);
            var reviews = await discovery.ListOpenReviewsAsync(clientId, repository, null, ct);
            return this.Ok(
                reviews
                    .Where(review => review.ReviewState is CodeReviewState.Open or CodeReviewState.Draft &&
                                     review.Provider == target.Provider &&
                                     string.Equals(
                                         review.Repository.ExternalRepositoryId,
                                         repositoryTarget.RepositoryId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(review => review.CodeReview.Number)
                    .Take(100)
                    .Select(review => new ClientOpenReviewResponse(
                        review.CodeReview.Number,
                        review.Title.Length <= 512 ? review.Title : review.Title[..512],
                        SafeWebUrl(review.WebUrl),
                        review.ReviewState == CodeReviewState.Draft ? "draft" : "open"))
                    .ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return this.StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "The provider could not list open pull requests." });
        }
    }

    /// <summary>Creates an inactive review target for one repository.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="request">Verified connection and repository coordinates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The identical target already exists.</response>
    /// <response code="201">The target was created.</response>
    /// <response code="400">The coordinates or connection state are invalid.</response>
    /// <response code="403">The caller cannot administer this client.</response>
    /// <response code="409">The project already has a different configuration or the capability is unavailable.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ClientReviewTargetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ClientReviewTargetResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTarget(
        Guid clientId,
        [FromBody] CreateClientReviewTargetRequest request,
        CancellationToken ct = default)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (denied is not null)
        {
            return denied;
        }

        var unavailable = await this.RequireCapabilityAsync(ct);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var project = request.ProviderProjectKey?.Trim();
        var repositoryId = request.RepositoryId?.Trim();
        var repositoryName = request.RepositoryName?.Trim();
        if (request.ConnectionId == Guid.Empty || string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(repositoryName) ||
            project.Length > 512 || repositoryId.Length > 512 || repositoryName.Length > 200)
        {
            return this.BadRequest(new { error = "Connection, project, repository ID, and repository name are required." });
        }

        var connection = await connections.GetByIdAsync(clientId, request.ConnectionId, ct);
        if (connection is null || !connection.IsActive ||
            !string.Equals(connection.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequest(new { error = "The connection must belong to this client and be active and verified." });
        }

        var scope = connection.ProviderFamily == ScmProvider.AzureDevOps
            ? request.ProviderScopePath?.Trim().TrimEnd('/')
            : connection.HostBaseUrl.TrimEnd('/');
        if (connection.ProviderFamily != ScmProvider.AzureDevOps &&
            !string.IsNullOrWhiteSpace(request.ProviderScopePath) &&
            !string.Equals(request.ProviderScopePath.Trim().TrimEnd('/'), scope, StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequest(new { error = "The provider scope must match the connection host." });
        }

        if (!TryGetOrigin(connection.HostBaseUrl, out var connectionOrigin) ||
            !TryGetOrigin(scope, out var scopeOrigin) ||
            !string.Equals(connectionOrigin, scopeOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequest(new { error = "The provider scope must use the connection host origin." });
        }

        if (connection.ProviderFamily == ScmProvider.AzureDevOps &&
            (!Uri.TryCreate(scope, UriKind.Absolute, out var scopeUrl) ||
             !string.IsNullOrEmpty(scopeUrl.Query) || !string.IsNullOrEmpty(scopeUrl.Fragment) ||
             !string.Equals(scopeUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'), scope, StringComparison.Ordinal)))
        {
            return this.BadRequest(new { error = "The Azure DevOps organization scope must be a canonical URL." });
        }

        if (providerRegistry is null)
        {
            return this.NotFound();
        }

        IRepositoryDiscoveryProvider discovery;
        try
        {
            discovery = providerRegistry.GetRepositoryDiscoveryProvider(connection.ProviderFamily);
        }
        catch (InvalidOperationException)
        {
            return this.NotFound();
        }

        IReadOnlyList<RepositoryRef> repositories;
        try
        {
            var host = new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl);
            if (connection.ProviderFamily == ScmProvider.AzureDevOps)
            {
                var configuredScopes = (await discovery.ListScopesAsync(clientId, host, ct))
                    .Where(configured => MatchesScope(configured, scope))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();
                if (configuredScopes.Count == 0)
                {
                    return this.BadRequest(new { error = "The Azure DevOps organization is not configured for this connection." });
                }

                if (configuredScopes.Count > 1)
                {
                    return this.Conflict(new { error = "Several Azure DevOps organizations match this scope." });
                }

                scope = configuredScopes[0].TrimEnd('/');
            }

            var discoveryScope = connection.ProviderFamily == ScmProvider.AzureDevOps ? scope! : project;
            repositories = await discovery.ListRepositoriesAsync(clientId, host, discoveryScope, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return this.BadRequest(new { error = "The provider refused repository discovery." });
        }

        var matches = repositories
            .Select(ToRepositoryResponse)
            .Where(repository =>
                string.Equals(repository.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(repository.RepositoryName, repositoryName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(repository.ProviderProjectKey, project, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matches.Count == 0)
        {
            return this.BadRequest(new { error = "The repository was not found in the selected provider scope." });
        }

        if (matches.Count > 1)
        {
            return this.Conflict(new { error = "Several repositories match the selected coordinates." });
        }

        var selected = matches[0];

        var existing = (await configurations.GetByClientAsync(clientId, ct))
            .FirstOrDefault(config => MatchesScope(config.ProviderScopePath, scope) &&
                                      string.Equals(config.ProviderProjectKey, selected.ProviderProjectKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var target = ToTarget(existing);
            return target is not null &&
                   string.Equals(target.RepositoryId, selected.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(target.RepositoryName, selected.RepositoryName, StringComparison.OrdinalIgnoreCase) &&
                   existing.Provider == connection.ProviderFamily
                ? this.Ok(target)
                : this.Conflict(new { error = "A different review target exists for this scope and project." });
        }

        try
        {
            var created = await configurations.AddReviewTargetAsync(
                clientId, connection.ProviderFamily, scope!, selected.ProviderProjectKey,
                selected.RepositoryId, selected.RepositoryName, ct);
            return this.StatusCode(StatusCodes.Status201Created, ToTarget(created));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var raced = (await configurations.GetByClientAsync(clientId, ct))
                .FirstOrDefault(config => config.ProviderScopePath == scope && config.ProviderProjectKey == project);
            var target = raced is null ? null : ToTarget(raced);
            return target is not null && target.RepositoryId == repositoryId &&
                   target.RepositoryName == repositoryName && raced!.Provider == connection.ProviderFamily
                ? this.Ok(target)
                : this.Conflict(new { error = "A different review target exists for this scope and project." });
        }
    }

    private async Task<IActionResult?> RequireCapabilityAsync(CancellationToken ct)
    {
        var unavailable = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(licensingCapabilityService, PremiumCapabilityKey.CrawlConfigs, ct);
        return unavailable is null ? null : new PremiumFeatureUnavailableResult(unavailable);
    }

    private static ClientReviewTargetResponse? ToTarget(CrawlConfigurationDto config)
    {
        if (config.RepoFilters.Count != 1 || config.RepoFilters[0].CanonicalSourceRef is not { } reference)
        {
            return null;
        }

        return new ClientReviewTargetResponse(
            config.Id, config.ProviderScopePath, config.ProviderProjectKey,
            reference.Value, config.RepoFilters[0].RepositoryName, config.IsActive);
    }

    private static ClientReviewTargetRepositoryResponse ToRepositoryResponse(RepositoryRef repository)
    {
        var name = repository.RepositoryName == repository.ExternalRepositoryId &&
                   repository.ProjectPath.Contains('/')
            ? repository.ProjectPath[(repository.ProjectPath.LastIndexOf('/') + 1)..]
            : repository.RepositoryName;
        return new ClientReviewTargetRepositoryResponse(
            repository.ExternalRepositoryId,
            name,
            repository.OwnerOrNamespace,
            repository.OwnerOrNamespace,
            repository.ProjectDisplayName ?? repository.OwnerOrNamespace);
    }

    private static bool TryGetOrigin(string? value, out string? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo))
        {
            return false;
        }

        origin = url.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static string? SafeWebUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(url.UserInfo))
        {
            return null;
        }

        return value;
    }

    private static bool MatchesBaseUrl(string connectionUrl, string targetUrl)
    {
        if (!Uri.TryCreate(connectionUrl, UriKind.Absolute, out var connection) ||
            !Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) ||
            !string.IsNullOrEmpty(connection.Query) || !string.IsNullOrEmpty(connection.Fragment) ||
            !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
        {
            return false;
        }

        return string.Equals(
                   connection.GetLeftPart(UriPartial.Authority),
                   target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   connection.AbsolutePath.TrimEnd('/'),
                   target.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static bool MatchesScope(string? left, string? right) =>
        string.Equals(left?.TrimEnd('/'), right?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Repository coordinates for a client review target.</summary>
public sealed record CreateClientReviewTargetRequest(
    Guid ConnectionId,
    string ProviderProjectKey,
    string RepositoryId,
    string RepositoryName,
    string? ProviderScopePath = null);

/// <summary>Client review target details.</summary>
public sealed record ClientReviewTargetResponse(
    Guid Id,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    string RepositoryName,
    bool IsActive);

/// <summary>A repository reachable through a verified provider connection.</summary>
public sealed record ClientReviewTargetRepositoryResponse(
    string RepositoryId,
    string RepositoryName,
    string ProviderProjectKey,
    string OwnerOrNamespace,
    string ProviderProjectDisplayName);

/// <summary>Open pull request details available to a review target.</summary>
public sealed record ClientOpenReviewResponse(
    int Number,
    string Title,
    string? WebUrl,
    string State);
