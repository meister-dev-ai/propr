// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using MeisterDev.ProPR.Api.Features.Crawling.Contracts.AzureDevOps;

namespace MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;

/// <summary>Manages repository review targets within one client.</summary>
[ApiController]
[Route("clients/{clientId:guid}/review-targets")]
public sealed class ClientReviewTargetsController(
    ICrawlConfigurationRepository configurations,
    IClientScmConnectionRepository connections,
    ILicensingCapabilityService? licensingCapabilityService = null,
    IScmProviderRegistry? providerRegistry = null,
    IClientScmScopeRepository? scopes = null,
    IClientPullRequestOverviewService? overview = null) : ControllerBase
{
    /// <summary>Returns a rich pull request page with shared source freshness and immutable generation navigation.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="request">Authorized source selections, page bounds and retained generation cursor.</param>
    /// <param name="ct">Caller cancellation token; shared provider work retains its bounded lifetime.</param>
    /// <response code="200">Pull requests, metadata, source outcomes and refresh availability; bounded capacity refusals return no rows or cursor and a null total.</response>
    /// <response code="400">The page bounds or retained generation are invalid or obsolete.</response>
    /// <response code="403">Current client or source access is unavailable.</response>
    /// <response code="409">The crawl configuration capability is unavailable.</response>
    /// <response code="503">Shared page storage is unavailable.</response>
    [HttpPost("overview")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ClientPullRequestOverviewPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetOverview(Guid clientId, [FromBody] ClientPullRequestOverviewRequest request, CancellationToken ct = default)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (denied is not null)
        {
            return denied;
        }

        if (request.Page > 1 && request.Cursor is null)
        {
            return this.BadRequest();
        }

        var unavailable = await this.RequireCapabilityAsync(ct);
        if (unavailable is not null)
        {
            return unavailable;
        }

        if (overview is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return this.Ok(await overview.ReadAsync(clientId, request, ct));
        }
        catch (ClientPullRequestOverviewException exception)
        {
            return this.StatusCode(exception.Kind == "accessDenied" ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest);
        }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException or HttpRequestException ||
                                          exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Reads one owned saved repository target, including removed targets, without provider access.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="targetId">Saved repository target identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The authoritative saved target and revision.</response>
    /// <response code="403">The caller cannot access this client.</response>
    /// <response code="404">The client does not own a canonical repository target with this identifier.</response>
    /// <response code="409">The configuration capability is unavailable.</response>
    [HttpGet("management/{targetId:guid}")]
    [ProducesResponseType(typeof(ClientReviewTargetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetManagementTarget(Guid clientId, Guid targetId, CancellationToken ct = default)
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

        var target = await configurations.GetReviewTargetPolicySnapshotAsync(targetId, clientId, ct);
        return target is null || target.ClientId != clientId || target.Id != targetId || ToTarget(target) is not { } saved
            ? this.NotFound()
            : this.Ok(saved);
    }

    /// <summary>Searches and pages saved repository metadata without contacting a provider.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="search">Repository name, project, or host search.</param>
    /// <param name="provider">Optional provider family filter.</param>
    /// <param name="status">Optional enabled or disabled review availability filter.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Rows per page, from 1 to 100.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The owned saved configuration page and opaque version of its entire matched representation.</response>
    /// <response code="400">The filters or bounds are invalid.</response>
    /// <response code="403">The caller cannot access this client.</response>
    /// <response code="409">The configuration capability is unavailable.</response>
    [HttpGet("management")]
    [ProducesResponseType(typeof(ClientReviewTargetPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetManagementTargets(
        Guid clientId, [FromQuery] string? search = null,
        [FromQuery] string? provider = null, [FromQuery] string? status = null, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken ct = default)
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

        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue || search?.Length > 512 ||
            !ScmProviderVocabulary.TryParseManagement(provider, out var providerFilter) || status is not (null or "enabled" or "disabled"))
        {
            return this.BadRequest(new { error = "Select valid filters and page bounds." });
        }

        ReviewTargetLifecycle? lifecycleFilter =
            status switch { "enabled" => ReviewTargetLifecycle.Enabled, "disabled" => ReviewTargetLifecycle.Disabled, _ => null };
        var result = await configurations.GetManagementTargetPageAsync(clientId, search, providerFilter, lifecycleFilter, page, pageSize, ct);
        return this.Ok(
            new ClientReviewTargetPageResponse(
                result.Items.Where(c => c.ClientId == clientId).Select(ToTarget).OfType<ClientReviewTargetResponse>().ToList(), result.TotalCount, result.Page,
                result.PageSize, result.SnapshotVersion));
    }

    /// <summary>Conditionally enables, disables, or removes a saved repository target.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="targetId">Saved repository target identifier.</param>
    /// <param name="request">Expected revision, lifecycle, and removal confirmation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The saved lifecycle and revision.</response>
    /// <response code="400">The lifecycle or confirmation is invalid.</response>
    /// <response code="403">The caller cannot administer this client.</response>
    /// <response code="404">The client does not own this target.</response>
    /// <response code="409">The target changed or the configuration capability is unavailable.</response>
    [HttpPatch("{targetId:guid}/lifecycle")]
    [ProducesResponseType(typeof(ClientReviewTargetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeLifecycle(
        Guid clientId, Guid targetId, [FromBody] ChangeClientReviewTargetLifecycleRequest request, CancellationToken ct = default)
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

        if (request.Lifecycle is not ("enabled" or "disabled" or "removed") ||
            !long.TryParse(
                request.ExpectedRevision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) ||
            revision < 1)
        {
            return this.BadRequest(new { error = "Select a valid lifecycle and saved revision." });
        }

        var target = await configurations.GetReviewTargetPolicySnapshotAsync(targetId, clientId, ct);
        if (target is null || ToTarget(target) is not { } saved)
        {
            return this.NotFound();
        }

        if (request.Lifecycle == "removed" && !string.Equals(request.ConfirmationName, saved.RepositoryName, StringComparison.Ordinal))
        {
            return this.BadRequest(new { error = "Enter the repository name to confirm removal." });
        }

        var lifecycle = request.Lifecycle switch
        {
            "enabled" => ReviewTargetLifecycle.Enabled, "disabled" => ReviewTargetLifecycle.Disabled, _ => ReviewTargetLifecycle.Removed
        };
        if (!await configurations.ChangeReviewTargetLifecycleAsync(targetId, clientId, revision, lifecycle, ct))
        {
            return this.Conflict(new { error = "The review target changed. Reload and retry." });
        }

        var current = await configurations.GetReviewTargetPolicySnapshotAsync(targetId, clientId, ct);
        return current is null ? this.Conflict(new { error = "The review target changed. Reload and retry." }) : this.Ok(ToTarget(current));
    }

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
        if (!IsOwnedVerifiedConnection(connection, clientId, connectionId) || !TryGetOrigin(connection.HostBaseUrl, out var connectionOrigin))
        {
            return this.BadRequest(new { error = "The connection must belong to this client and be active and verified." });
        }

        var selectedScope = scopePath?.Trim();
        if (string.IsNullOrWhiteSpace(selectedScope))
        {
            return this.BadRequest(new { error = "The provider scope is invalid for this connection." });
        }

        if (providerRegistry is null)
        {
            return this.NotFound();
        }

        var sourcePolicy = this.TryGetSourcePolicy(connection.ProviderFamily);
        if (sourcePolicy is null)
        {
            return this.NotFound();
        }

        if (!sourcePolicy.IsDiscoveryScopeValid(connectionOrigin!, selectedScope))
        {
            return this.BadRequest(new { error = "The provider scope is invalid for this connection." });
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
            .Where(configuration => configuration.ClientId == clientId && configuration.ReviewTargetLifecycle != ReviewTargetLifecycle.Removed)
            .Select(ToTarget)
            .Where(target => target is not null)
            .Cast<ClientReviewTargetResponse>()
            .ToList();
        return this.Ok(targets);
    }

    /// <summary>Lists up to 100 open pull requests using the selected verified connection and saved target scope.</summary>
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
            .FirstOrDefault(configuration => configuration.Id == targetId && configuration.ClientId == clientId);
        if (target is null || target.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled || ToTarget(target) is not { } repositoryTarget)
        {
            return this.NotFound();
        }

        var connection = await connections.GetByIdAsync(clientId, connectionId, ct);
        if (!IsOwnedVerifiedConnection(connection, clientId, connectionId) || !MatchesTargetOrigin(connection, target))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
        }

        if (providerRegistry is null)
        {
            return this.NotFound();
        }

        var sourcePolicy = this.TryGetSourcePolicy(target.Provider);
        if (sourcePolicy is null)
        {
            return this.NotFound();
        }

        if (!sourcePolicy.IsTargetScopeCompatible(connection.HostBaseUrl, target.ProviderScopePath))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
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
            if (!DestinationBranchPolicy.TryCreate(repositoryTarget.TargetBranchPatterns, out var policy))
            {
                return this.Conflict(new { error = "The stored destination branch policy is invalid." });
            }

            var repository = sourcePolicy.CreateRepository(
                connection.HostBaseUrl, repositoryTarget.RepositoryId, target.ProviderProjectKey, repositoryTarget.RepositoryName);
            var reviews = await discovery.ListOpenReviewsAsync(
                clientId, repository, null, ct, new ReviewDiscoveryContext(connectionId, target.ProviderScopePath));
            return this.Ok(
                reviews
                    .Where(review => review.ReviewState is CodeReviewState.Open or CodeReviewState.Draft &&
                                     review.Provider == target.Provider &&
                                     policy!.Matches(review.TargetBranch) &&
                                     string.Equals(
                                         review.Repository.ExternalRepositoryId,
                                         repositoryTarget.RepositoryId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(review => review.CodeReview.Number)
                    .Take(100)
                    .Select(review => new ClientOpenReviewResponse(
                        review.CodeReview.Number,
                        review.Title.Length <= 512 ? review.Title : review.Title[..512],
                        SafeWebUrl(review.WebUrl),
                        review.ReviewState == CodeReviewState.Draft ? "draft" : "open",
                        BoundedText(review.SourceBranch, 512),
                        BoundedText(review.TargetBranch, 512),
                        BoundedText(review.AuthorName?.Contains('@') == true ? null : review.AuthorName, 256),
                        BoundedText(review.ReviewRevision?.HeadSha, 128),
                        BoundedText(review.ReviewRevision?.ProviderRevisionId, 128)))
                    .ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            return this.StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "The provider could not list open pull requests." });
        }
    }

    /// <summary>Reads bounded comment and discussion metadata for one configured pull request.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="targetId">Configured review target identifier.</param>
    /// <param name="number">Positive provider-native pull request or merge request number.</param>
    /// <remarks>Installed adapters address reviews by their native number. The overview reconstructs the external review identifier from this number; opaque-only identifiers are not supported by this route.</remarks>
    /// <param name="connectionId">Verified selected connection identifier.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <response code="200">Counts and their completeness and resolution availability.</response>
    /// <response code="400">The pull request number or connection is invalid.</response>
    /// <response code="403">The caller cannot access this client.</response>
    /// <response code="404">The target or metadata capability is unavailable.</response>
    /// <response code="409">The crawl configuration capability is unavailable.</response>
    /// <response code="502">The selected provider could not return metadata.</response>
    [HttpGet("{targetId:guid}/open-reviews/{number:int}/metadata")]
    [ProducesResponseType(typeof(ReviewOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetOpenReviewMetadata(
        Guid clientId, Guid targetId, int number, [FromQuery] Guid connectionId, CancellationToken ct = default)
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

        if (number <= 0)
        {
            return this.BadRequest(new { error = "The pull request number must be positive." });
        }

        var target = (await configurations.GetByClientAsync(clientId, ct))
            .FirstOrDefault(configuration => configuration.Id == targetId && configuration.ClientId == clientId);
        if (target is null || target.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled || ToTarget(target) is not { } repositoryTarget)
        {
            return this.NotFound();
        }

        var connection = await connections.GetByIdAsync(clientId, connectionId, ct);
        if (!IsOwnedVerifiedConnection(connection, clientId, connectionId) || !MatchesTargetOrigin(connection, target))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
        }

        if (providerRegistry is null)
        {
            return this.NotFound();
        }

        var sourcePolicy = this.TryGetSourcePolicy(target.Provider);
        if (sourcePolicy is null)
        {
            return this.NotFound();
        }

        if (!sourcePolicy.IsTargetScopeCompatible(connection.HostBaseUrl, target.ProviderScopePath))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
        }

        IReviewOverviewProvider overview;
        try
        {
            overview = providerRegistry.GetReviewOverviewProvider(target.Provider);
        }
        catch (InvalidOperationException)
        {
            return this.NotFound();
        }

        try
        {
            var repository = providerRegistry.GetReviewSourcePolicy(target.Provider).CreateRepository(
                connection.HostBaseUrl, repositoryTarget.RepositoryId, target.ProviderProjectKey, repositoryTarget.RepositoryName);
            var review = new CodeReviewRef(
                repository, CodeReviewPlatformKind.PullRequest,
                number.ToString(System.Globalization.CultureInfo.InvariantCulture), number);
            return this.Ok(
                await overview.GetOverviewAsync(
                    clientId, review,
                    new ReviewDiscoveryContext(connectionId, target.ProviderScopePath), ct));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException or System.Text.Json.JsonException)
        {
            return this.StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "The provider could not return pull request metadata." });
        }
    }

    /// <summary>Creates an inactive review target for one repository.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="request">Verified connection, repository coordinates, and optional destination branch patterns.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The identical target already exists.</response>
    /// <response code="201">The target was created.</response>
    /// <response code="400">The coordinates or connection state are invalid.</response>
    /// <response code="403">The caller cannot administer this client.</response>
    /// <response code="409">The repository identity conflicts with stored configuration or the capability is unavailable.</response>
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

        if (!DestinationBranchPolicy.TryCreate(request.TargetBranchPatterns, out var policy))
        {
            return this.BadRequest(
                new
                {
                    errors = new
                    {
                        targetBranchPatterns = new[] { "Enter up to 100 unique destination branch patterns of 1 to 512 characters without control characters." }
                    }
                });
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
        if (!IsOwnedVerifiedConnection(connection, clientId, request.ConnectionId))
        {
            return this.BadRequest(new { error = "The connection must belong to this client and be active and verified." });
        }

        var sourcePolicy = this.TryGetSourcePolicy(connection.ProviderFamily);
        if (sourcePolicy is null)
        {
            return this.NotFound();
        }

        var scope = sourcePolicy.SelectTargetScope(connection.HostBaseUrl, request.ProviderScopePath);
        if (!sourcePolicy.RequiresOrganizationScope &&
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

        if (!sourcePolicy.IsTargetScopeCanonical(scope))
        {
            return this.BadRequest(new { error = AdoConfigurationCompatibilityMessages.CanonicalOrganizationScope });
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
            if (sourcePolicy.RequiresOrganizationScope)
            {
                var configuredScopes = (await discovery.ListScopesAsync(clientId, host, ct))
                    .Where(configured => MatchesScope(configured, scope))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToList();
                if (configuredScopes.Count == 0)
                {
                    return this.BadRequest(new { error = AdoConfigurationCompatibilityMessages.OrganizationScopeUnavailable });
                }

                if (configuredScopes.Count > 1)
                {
                    return this.Conflict(new { error = AdoConfigurationCompatibilityMessages.AmbiguousOrganizations });
                }

                scope = configuredScopes[0].TrimEnd('/');
            }

            var discoveryScope = sourcePolicy.GetRepositoryDiscoveryScope(scope!, project);
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

        var existing = FindMatchingTargets(
            (await configurations.GetByClientAsync(clientId, ct)).Where(config => config.ClientId == clientId).ToList(), connection.ProviderFamily, scope!,
            selected.ProviderProjectKey, selected.RepositoryId);
        if (existing.Count > 1)
        {
            return this.Conflict(new { error = "Several review targets match the repository identity." });
        }

        if (existing.Count == 1)
        {
            var target = ToTarget(existing[0])!;
            if (existing[0].ReviewTargetLifecycle == ReviewTargetLifecycle.Removed)
            {
                if (!request.RestoreRemovedTarget || request.ExpectedRemovedRevision != target.Revision)
                {
                    return this.Conflict(new { error = "Confirm restoration of the removed repository.", removedTarget = target });
                }

                if (!await configurations.RestoreReviewTargetAsync(existing[0], clientId, policy!.Patterns, ct))
                {
                    return this.Conflict(new { error = "The removed repository changed. Reload and retry." });
                }

                var restored = await configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, ct);
                return this.Ok(ToTarget(restored!));
            }

            return string.Equals(target.RepositoryName, selected.RepositoryName, StringComparison.OrdinalIgnoreCase)
                   && target.TargetBranchPatterns.SequenceEqual(policy!.Patterns, StringComparer.OrdinalIgnoreCase)
                ? this.Ok(target)
                : this.Conflict(new { error = "The stored review target has different repository coordinates." });
        }

        try
        {
            var created = await configurations.AddReviewTargetAsync(
                clientId, connection.ProviderFamily, scope!, selected.ProviderProjectKey,
                selected.RepositoryId, selected.RepositoryName, ct, policy!.Patterns.Count == 0 ? null : policy.Patterns);
            return this.StatusCode(StatusCodes.Status201Created, ToTarget(created));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var raced = FindMatchingTargets(
                (await configurations.GetByClientAsync(clientId, ct)).Where(config => config.ClientId == clientId).ToList(), connection.ProviderFamily, scope!,
                selected.ProviderProjectKey, selected.RepositoryId);
            var target = raced.Count == 1 ? ToTarget(raced[0]) : null;
            return target is not null && target.Lifecycle != "removed" && string.Equals(
                       target.RepositoryName, selected.RepositoryName, StringComparison.OrdinalIgnoreCase)
                   && target.TargetBranchPatterns.SequenceEqual(policy!.Patterns, StringComparer.OrdinalIgnoreCase)
                ? this.Ok(target)
                : this.Conflict(new { error = "The repository identity conflicts with stored configuration." });
        }
    }

    /// <summary>Conditionally replaces a repository target's destination branch policy.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="targetId">Saved repository target identifier.</param>
    /// <param name="request">Verified connection, expected stored patterns, and replacement patterns.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The policy was saved without changing target activation or settings.</response>
    /// <response code="400">The patterns or connection are invalid.</response>
    /// <response code="403">The caller cannot administer this client.</response>
    /// <response code="404">The client does not own this canonical repository target.</response>
    /// <response code="409">The target identity or stored policy changed, or the capability is unavailable.</response>
    [HttpPatch("{targetId:guid}")]
    [ProducesResponseType(typeof(ClientReviewTargetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateTargetPolicy(
        Guid clientId, Guid targetId, [FromBody] UpdateClientReviewTargetPolicyRequest request, CancellationToken ct = default)
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

        if (request.ConnectionId == Guid.Empty)
        {
            return this.BadRequest(new { error = "Select a verified connection for this review target." });
        }

        if (request.TargetBranchPatterns is null || request.ExpectedTargetBranchPatterns is null ||
            !DestinationBranchPolicy.TryCreate(request.TargetBranchPatterns, out var policy) ||
            !DestinationBranchPolicy.TryCreate(request.ExpectedTargetBranchPatterns, out _))
        {
            return this.BadRequest(
                new
                {
                    errors = new
                    {
                        targetBranchPatterns = new[] { "Enter up to 100 unique destination branch patterns of 1 to 512 characters without control characters." }
                    }
                });
        }

        var target = (await configurations.GetByClientAsync(clientId, ct))
            .FirstOrDefault(config => config.Id == targetId && config.ClientId == clientId);
        if (target is null || target.ReviewTargetLifecycle == ReviewTargetLifecycle.Removed || ToTarget(target) is not { } repositoryTarget)
        {
            return this.NotFound();
        }

        var connection = await connections.GetByIdAsync(clientId, request.ConnectionId, ct);
        if (!IsOwnedVerifiedConnection(connection, clientId, request.ConnectionId) || !MatchesTargetOrigin(connection, target))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
        }

        var sourcePolicy = this.TryGetSourcePolicy(target.Provider);
        if (sourcePolicy is null)
        {
            return this.NotFound();
        }

        if (!sourcePolicy.IsTargetScopeCompatible(connection.HostBaseUrl, target.ProviderScopePath))
        {
            return this.BadRequest(new { error = "The connection must be active, verified, and cover this review target." });
        }

        if (sourcePolicy.RequiresOrganizationScope &&
            (scopes is null || !(await scopes.GetByConnectionIdAsync(clientId, connection.Id, ct)).Any(scope =>
                scope.ClientId == clientId && scope.ConnectionId == connection.Id && scope.IsEnabled &&
                sourcePolicy.MatchesOrganizationScope(scope, target.ProviderScopePath))))
        {
            return this.BadRequest(new { error = AdoConfigurationCompatibilityMessages.OrganizationScopeUnavailable });
        }

        if (!await configurations.UpdateReviewTargetPolicyAsync(target, clientId, request.ExpectedTargetBranchPatterns, policy!.Patterns, ct))
        {
            return this.Conflict(new { error = "The destination branch policy changed. Reload this target and retry." });
        }

        var current = await configurations.GetReviewTargetPolicySnapshotAsync(targetId, clientId, ct);
        return current?.ClientId == clientId && ToTarget(current) is { } saved
            ? this.Ok(saved)
            : this.Conflict(new { error = "The review target changed. Reload this target and retry." });
    }


    private static bool IsOwnedVerifiedConnection([NotNullWhen(true)] ClientScmConnectionDto? connection, Guid clientId, Guid connectionId)
    {
        if (connection is null || connection.ClientId != clientId || connection.Id != connectionId)
        {
            return false;
        }

        return connection.IsActive && string.Equals(connection.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTargetOrigin(ClientScmConnectionDto connection, CrawlConfigurationDto target)
    {
        if (connection.ProviderFamily != target.Provider)
        {
            return false;
        }

        if (!TryGetOrigin(connection.HostBaseUrl, out var connectionOrigin) || !TryGetOrigin(target.ProviderScopePath, out var targetOrigin))
        {
            return false;
        }

        return string.Equals(connectionOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private IReviewSourcePolicy? TryGetSourcePolicy(ScmProvider provider)
    {
        if (providerRegistry is null)
        {
            return null;
        }

        try
        {
            return providerRegistry.GetReviewSourcePolicy(provider);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<CrawlConfigurationDto> FindMatchingTargets(
        IReadOnlyList<CrawlConfigurationDto> configs, ScmProvider provider, string scope, string project, string repositoryId) =>
        configs.Where(config => config.Provider == provider && MatchesScope(config.ProviderScopePath, scope) &&
                                string.Equals(config.ProviderProjectKey, project, StringComparison.OrdinalIgnoreCase) &&
                                ToTarget(config) is { } target &&
                                string.Equals(target.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToList();

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
            reference.Value, config.RepoFilters[0].RepositoryName, config.IsActive,
            ScmProviderVocabulary.ToPublicName(config.Provider), config.RepoFilters[0].TargetBranchPatterns,
            config.ReviewTargetLifecycle.ToString().ToLowerInvariant(),
            config.ReviewTargetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    private static string? BoundedText(string? value, int limit) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= limit ? value : value[..limit];

    private static bool MatchesScope(string? left, string? right) =>
        string.Equals(left?.TrimEnd('/'), right?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Repository coordinates for a client review target.</summary>
public sealed record CreateClientReviewTargetRequest(
    Guid ConnectionId,
    string ProviderProjectKey,
    string RepositoryId,
    string RepositoryName,
    string? ProviderScopePath = null,
    IReadOnlyList<string>? TargetBranchPatterns = null,
    bool RestoreRemovedTarget = false,
    string? ExpectedRemovedRevision = null);

/// <summary>A conditional saved repository lifecycle transition.</summary>
public sealed record ChangeClientReviewTargetLifecycleRequest(string ExpectedRevision, string Lifecycle, string? ConfirmationName = null);

/// <summary>A server-filtered page of saved repository configuration.</summary>
/// <summary>A bounded saved-target page with a version for the entire matched representation.</summary>
/// <param name="Items">Saved target rows.</param>
/// <param name="TotalCount">Number of matched targets.</param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Maximum rows per page.</param>
/// <param name="SnapshotVersion">Opaque lowercase SHA-256 version of all matched target metadata.</param>
public sealed record ClientReviewTargetPageResponse(
    IReadOnlyList<ClientReviewTargetResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    string SnapshotVersion = "");

/// <summary>A policy-only conditional update for a saved repository target.</summary>
public sealed class UpdateClientReviewTargetPolicyRequest(
    Guid connectionId,
    IReadOnlyList<string> expectedTargetBranchPatterns,
    IReadOnlyList<string> targetBranchPatterns)
{
    /// <summary>Selected verified connection identifier.</summary>
    [Required]
    public Guid ConnectionId { get; init; } = connectionId;

    /// <summary>Required raw stored policy snapshot; an empty array represents All.</summary>
    [Required]
    public IReadOnlyList<string> ExpectedTargetBranchPatterns { get; init; } = expectedTargetBranchPatterns;

    /// <summary>Required replacement policy; an empty array represents All.</summary>
    [Required]
    public IReadOnlyList<string> TargetBranchPatterns { get; init; } = targetBranchPatterns;
}

/// <summary>Client review target details.</summary>
public sealed record ClientReviewTargetResponse(
    Guid Id,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    string RepositoryName,
    bool IsActive,
    string ProviderFamily,
    IReadOnlyList<string> TargetBranchPatterns,
    string Lifecycle = "enabled",
    string Revision = "1");

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
    string State,
    string? SourceBranch = null,
    string? TargetBranch = null,
    string? AuthorName = null,
    string? HeadSha = null,
    string? ProviderRevisionId = null);
