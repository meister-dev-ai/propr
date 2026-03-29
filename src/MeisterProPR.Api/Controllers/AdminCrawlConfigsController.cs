using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoints for managing crawl configurations across all clients.</summary>
[ApiController]
public sealed partial class AdminCrawlConfigsController(
    ICrawlConfigurationRepository crawlConfigRepo,
    IUserRepository userRepository,
    IClientAdminService clientAdminService,
    ILogger<AdminCrawlConfigsController> logger) : ControllerBase
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Crawl config {ConfigId} created for client {ClientId} by admin")]
    private static partial void LogCrawlConfigCreated(ILogger logger, Guid configId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Crawl config {ConfigId} deleted by admin")]
    private static partial void LogCrawlConfigDeleted(ILogger logger, Guid configId);

    private static CrawlConfigResponse ToCrawlConfigResponse(CrawlConfigurationDto c) =>
        new(
            c.Id,
            c.ClientId,
            c.OrganizationUrl,
            c.ProjectId,
            c.CrawlIntervalSeconds,
            c.IsActive,
            c.CreatedAt,
            c.RepoFilters
                .Select(f => new CrawlRepoFilterResponse(f.Id, f.RepositoryName, f.TargetBranchPatterns))
                .ToList()
                .AsReadOnly());

    private IActionResult? Validate(ValidationResult result)
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

    private IActionResult? RequireAuth(out bool isAdmin, out Guid? userId)
    {
        isAdmin = this.HttpContext.Items["IsAdmin"] is true;
        userId = this.HttpContext.Items["UserId"] is string s && Guid.TryParse(s, out var id) ? id : null;

        if (!isAdmin && userId is null)
        {
            return this.Unauthorized(new { error = "Authentication required." });
        }
        return null;
    }

    /// <summary>
    ///     Lists all accessible crawl configurations.
    ///     Admins receive all configs; non-Admin users receive only configs for their assigned clients.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of crawl configurations.</response>
    /// <response code="401">No valid credentials provided.</response>
    [HttpGet("/admin/crawl-configurations")]
    [ProducesResponseType(typeof(IReadOnlyList<CrawlConfigResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCrawlConfigurations(CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out var userId);
        if (auth is not null)
        {
            return auth;
        }

        IReadOnlyList<CrawlConfigurationDto> configs;
        if (isAdmin)
        {
            configs = await crawlConfigRepo.GetAllActiveAsync(ct);
        }
        else
        {
            var user = await userRepository.GetByIdWithAssignmentsAsync(userId!.Value, ct);
            var clientIds = user?.ClientAssignments.Select(a => a.ClientId).ToList() ?? [];
            configs = clientIds.Count > 0
                ? await crawlConfigRepo.GetByClientIdsAsync(clientIds, ct)
                : [];
        }

        return this.Ok(configs.Select(static c => ToCrawlConfigResponse(c)));
    }

    /// <summary>
    ///     Creates a new crawl configuration.
    ///     Admins may create for any client; non-Admin users may only create for their assigned clients.
    /// </summary>
    /// <param name="request">Crawl configuration details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Configuration created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Non-Admin caller does not own the specified client.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">Config for this org/project already exists for the client.</response>
    [HttpPost("/admin/crawl-configurations")]
    [ProducesResponseType(typeof(CrawlConfigResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCrawlConfiguration(
        [FromBody] CreateAdminCrawlConfigRequest request,
        [FromServices] IValidator<CreateAdminCrawlConfigRequest> validator,
        CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out var userId);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.Validate(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (!isAdmin)
        {
            // Non-Admin: verify caller owns the specified client
            var user = await userRepository.GetByIdWithAssignmentsAsync(userId!.Value, ct);
            var ownedIds = user?.ClientAssignments.Select(a => a.ClientId).ToHashSet() ?? [];
            if (!ownedIds.Contains(request.ClientId))
            {
                return this.StatusCode(StatusCodes.Status403Forbidden);
            }
        }
        else
        {
            // Admin: verify the client exists
            if (!await clientAdminService.ExistsAsync(request.ClientId, ct))
            {
                return this.NotFound();
            }
        }

        if (await crawlConfigRepo.ExistsAsync(request.ClientId, request.OrganizationUrl, request.ProjectId, ct))
        {
            return this.Conflict(new { error = "A crawl configuration for this organisation and project already exists." });
        }

        var config = await crawlConfigRepo.AddAsync(
            request.ClientId, request.OrganizationUrl, request.ProjectId, request.CrawlIntervalSeconds, ct);

        LogCrawlConfigCreated(logger, config.Id, config.ClientId);

        return this.CreatedAtAction(
            nameof(this.GetCrawlConfigurations),
            null,
            ToCrawlConfigResponse(config));
    }

    /// <summary>
    ///     Applies partial updates to a crawl configuration.
    ///     Admins may update any config; non-Admin users may only update configs for their clients.
    /// </summary>
    /// <param name="configId">Configuration identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Configuration updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Non-Admin caller does not own this config's client.</response>
    /// <response code="404">Configuration not found.</response>
    [HttpPatch("/admin/crawl-configurations/{configId:guid}")]
    [ProducesResponseType(typeof(CrawlConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchCrawlConfiguration(
        Guid configId,
        [FromBody] PatchAdminCrawlConfigRequest request,
        [FromServices] IValidator<PatchAdminCrawlConfigRequest> validator,
        CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out var userId);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.Validate(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var existing = await crawlConfigRepo.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
            var user = await userRepository.GetByIdWithAssignmentsAsync(userId!.Value, ct);
            var ownedIds = user?.ClientAssignments.Select(a => a.ClientId).ToHashSet() ?? [];
            if (!ownedIds.Contains(existing.ClientId))
            {
                return this.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        Guid? ownerScope = isAdmin ? null : existing.ClientId;
        var updated = await crawlConfigRepo.UpdateAsync(
            configId, request.CrawlIntervalSeconds, request.IsActive, ownerScope, ct);

        if (!updated)
        {
            return this.NotFound();
        }

        // Update repo filters when explicitly provided (omitting the field = leave unchanged).
        if (request.RepoFilters is not null)
        {
            var filterDtos = request.RepoFilters
                .Select(f => new CrawlRepoFilterDto(Guid.Empty, f.RepositoryName, f.TargetBranchPatterns))
                .ToList();
            await crawlConfigRepo.UpdateRepoFiltersAsync(configId, filterDtos, ct);
        }

        var refreshed = await crawlConfigRepo.GetByIdAsync(configId, ct);
        if (refreshed is null)
        {
            return this.NotFound();
        }

        return this.Ok(ToCrawlConfigResponse(refreshed));
    }

    /// <summary>
    ///     Deletes a crawl configuration.
    ///     Admins may delete any config; non-Admin users may only delete configs for their clients.
    /// </summary>
    /// <param name="configId">Configuration identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Configuration deleted.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Non-Admin caller does not own this config's client.</response>
    /// <response code="404">Configuration not found.</response>
    [HttpDelete("/admin/crawl-configurations/{configId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCrawlConfiguration(Guid configId, CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out var userId);
        if (auth is not null)
        {
            return auth;
        }

        var existing = await crawlConfigRepo.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
            var user = await userRepository.GetByIdWithAssignmentsAsync(userId!.Value, ct);
            var ownedIds = user?.ClientAssignments.Select(a => a.ClientId).ToHashSet() ?? [];
            if (!ownedIds.Contains(existing.ClientId))
            {
                return this.StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        await crawlConfigRepo.DeleteAsync(configId, existing.ClientId, ct);
        LogCrawlConfigDeleted(logger, configId);
        return this.NoContent();
    }
}

/// <summary>Request body for creating an admin-managed crawl configuration.</summary>
public sealed record CreateAdminCrawlConfigRequest(
    Guid ClientId,
    string OrganizationUrl,
    string ProjectId,
    int CrawlIntervalSeconds = 60);

/// <summary>
///     Request body for patching an admin-managed crawl configuration.
///     Omit a field to leave it unchanged.
/// </summary>
public sealed record PatchAdminCrawlConfigRequest(
    int? CrawlIntervalSeconds = null,
    bool? IsActive = null,
    IReadOnlyList<CrawlRepoFilterRequest>? RepoFilters = null);

/// <summary>A single repo filter entry in a PATCH request.</summary>
public sealed record CrawlRepoFilterRequest(
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns);
