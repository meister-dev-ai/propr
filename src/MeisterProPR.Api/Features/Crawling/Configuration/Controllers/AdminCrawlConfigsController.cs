// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoints for managing crawl configurations across all clients.</summary>
[ApiController]
public sealed partial class AdminCrawlConfigsController(
    ICrawlConfigurationRepository crawlConfigRepo,
    IUserRepository userRepository,
    IClientAdminService clientAdminService,
    IScmProviderRegistry providerRegistry,
    IProCursorKnowledgeSourceRepository proCursorKnowledgeSourceRepository,
    ILogger<AdminCrawlConfigsController> logger,
    IProviderActivationService? providerActivationService = null,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private const string DisabledProviderMessage =
        "The selected provider family is currently disabled by system administration.";

    private static CrawlConfigResponse ToCrawlConfigResponse(CrawlConfigurationDto c)
    {
        return new CrawlConfigResponse(
            c.Id,
            c.ClientId,
            c.Provider,
            c.OrganizationScopeId,
            c.ProviderScopePath,
            c.ProviderProjectKey,
            c.CrawlIntervalSeconds,
            c.IsActive,
            c.CreatedAt,
            c.RepoFilters
                .Select(f => new CrawlRepoFilterResponse(
                    f.Id,
                    f.RepositoryName,
                    f.TargetBranchPatterns,
                    f.CanonicalSourceRef,
                    f.DisplayName))
                .ToList()
                .AsReadOnly(),
            c.ProCursorSourceScopeMode,
            c.ProCursorSourceIds,
            c.InvalidProCursorSourceIds);
    }

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
        isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        userId = AuthHelpers.GetUserId(this.HttpContext);
        return AuthHelpers.RequireAuthenticated(this.HttpContext);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<bool> IsProviderEnabledAsync(ScmProvider provider, CancellationToken ct)
    {
        return providerActivationService is null || await providerActivationService.IsEnabledAsync(provider, ct);
    }

    private async Task<IActionResult?> RequireCrawlConfigsCapabilityAsync(CancellationToken ct)
    {
        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.CrawlConfigs,
            ct);

        return capability is null ? null : new PremiumFeatureUnavailableResult(capability);
    }

    private static IReadOnlyList<string> NormalizeBranchPatterns(IReadOnlyList<string>? targetBranchPatterns)
    {
        return (targetBranchPatterns ?? [])
            .Select(pattern => pattern.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static string ResolveRepositoryName(CrawlRepoFilterRequest filter)
    {
        var repositoryName = NormalizeOptional(filter.RepositoryName)
                             ?? NormalizeOptional(filter.DisplayName)
                             ?? NormalizeOptional(filter.CanonicalSourceRef?.Value);

        return repositoryName
               ?? throw new InvalidOperationException("Each repository filter must include a repository name, display name, or canonical source reference.");
    }

    private async Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveOrganizationSelectionAsync(
        Guid clientId,
        ScmProvider provider,
        Guid? organizationScopeId,
        string? organizationUrl,
        CancellationToken ct)
    {
        if (provider != ScmProvider.AzureDevOps)
        {
            var normalizedProviderOrganizationUrl = NormalizeOptional(organizationUrl)
                                                    ?? throw new InvalidOperationException(
                                                        "ProviderScopePath is required for non-Azure DevOps crawl configurations.");

            return (null, normalizedProviderOrganizationUrl);
        }

        if (organizationScopeId.HasValue)
        {
            var scope = await providerRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                            .GetScopeAsync(clientId, organizationScopeId.Value, ct)
                        ?? throw new InvalidOperationException("The selected Azure DevOps organization is no longer available for this client.");

            if (!scope.IsEnabled)
            {
                throw new InvalidOperationException("The selected Azure DevOps organization is disabled.");
            }

            return (scope.Id, scope.ScopePath);
        }

        var normalizedOrganizationUrl = NormalizeOptional(organizationUrl)
                                        ?? throw new InvalidOperationException("ProviderScopePath is required when OrganizationScopeId is not provided.");

        return (null, normalizedOrganizationUrl);
    }

    private async Task<IReadOnlyList<CrawlRepoFilterDto>> ResolveRepoFiltersAsync(
        Guid clientId,
        ScmProvider provider,
        Guid? organizationScopeId,
        string projectId,
        IReadOnlyList<CrawlRepoFilterRequest>? repoFilters,
        CancellationToken ct)
    {
        if (repoFilters is null)
        {
            return [];
        }

        var filterDtos = new List<CrawlRepoFilterDto>(repoFilters.Count);
        IReadOnlyList<AdoCrawlFilterOptionDto> availableFilters = [];

        if (provider == ScmProvider.AzureDevOps && organizationScopeId.HasValue)
        {
            availableFilters = await providerRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                .ListCrawlFiltersAsync(clientId, organizationScopeId.Value, projectId, ct);
        }

        foreach (var filter in repoFilters)
        {
            var canonicalSourceRef = filter.CanonicalSourceRef;
            var normalizedProvider = NormalizeOptional(canonicalSourceRef?.Provider);
            var normalizedValue = NormalizeOptional(canonicalSourceRef?.Value);
            var normalizedDisplayName = NormalizeOptional(filter.DisplayName);
            var repositoryName = ResolveRepositoryName(filter);
            var targetBranchPatterns = NormalizeBranchPatterns(filter.TargetBranchPatterns);

            if (provider == ScmProvider.AzureDevOps && organizationScopeId.HasValue && normalizedProvider is not null &&
                normalizedValue is not null)
            {
                var matchedFilter = availableFilters.FirstOrDefault(option =>
                    string.Equals(
                        option.CanonicalSourceRef.Provider,
                        normalizedProvider,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        option.CanonicalSourceRef.Value,
                        normalizedValue,
                        StringComparison.OrdinalIgnoreCase));

                if (matchedFilter is null)
                {
                    throw new InvalidOperationException(
                        $"The selected crawl filter '{normalizedDisplayName ?? repositoryName}' is no longer available in Azure DevOps.");
                }

                filterDtos.Add(
                    new CrawlRepoFilterDto(
                        Guid.Empty,
                        repositoryName,
                        targetBranchPatterns,
                        new CanonicalSourceReferenceDto(normalizedProvider, normalizedValue),
                        normalizedDisplayName ?? matchedFilter.DisplayName));
                continue;
            }

            filterDtos.Add(
                new CrawlRepoFilterDto(
                    Guid.Empty,
                    repositoryName,
                    targetBranchPatterns,
                    normalizedProvider is not null && normalizedValue is not null
                        ? new CanonicalSourceReferenceDto(normalizedProvider, normalizedValue)
                        : null,
                    normalizedDisplayName));
        }

        return filterDtos.AsReadOnly();
    }

    private async Task<IReadOnlyList<Guid>> ValidateSelectedProCursorSourcesAsync(
        Guid clientId,
        ProCursorSourceScopeMode sourceScopeMode,
        IReadOnlyList<Guid>? proCursorSourceIds,
        CancellationToken ct)
    {
        if (sourceScopeMode == ProCursorSourceScopeMode.AllClientSources)
        {
            return [];
        }

        var selectedSourceIds = (proCursorSourceIds ?? [])
            .Where(sourceId => sourceId != Guid.Empty)
            .Distinct()
            .ToList();

        if (selectedSourceIds.Count == 0)
        {
            throw new InvalidOperationException("At least one ProCursor source must be selected when the crawl configuration uses selected sources.");
        }

        var availableSources = await proCursorKnowledgeSourceRepository.ListByClientAsync(clientId, ct);
        var invalidSourceIds = selectedSourceIds
            .Where(selectedSourceId =>
                !availableSources.Any(source => source.Id == selectedSourceId && source.IsEnabled))
            .ToList();

        if (invalidSourceIds.Count > 0)
        {
            throw new InvalidOperationException("One or more selected ProCursor sources are no longer eligible for this client.");
        }

        return selectedSourceIds.AsReadOnly();
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

        var capability = await this.RequireCrawlConfigsCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
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

        var capability = await this.RequireCrawlConfigsCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        if (!isAdmin)
        {
            // Non-Admin: require ClientAdministrator role for the target client
            var roleCheck = AuthHelpers.RequireClientRole(
                this.HttpContext,
                request.ClientId,
                ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
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

        if (!await this.IsProviderEnabledAsync(request.Provider, ct))
        {
            return this.Conflict(new { error = DisabledProviderMessage });
        }

        try
        {
            var resolvedOrganization = await this.ResolveOrganizationSelectionAsync(
                request.ClientId,
                request.Provider,
                request.OrganizationScopeId,
                request.ProviderScopePath,
                ct);
            var repoFilters = await this.ResolveRepoFiltersAsync(
                request.ClientId,
                request.Provider,
                resolvedOrganization.OrganizationScopeId,
                request.ProviderProjectKey.Trim(),
                request.RepoFilters,
                ct);
            var selectedProCursorSourceIds = await this.ValidateSelectedProCursorSourcesAsync(
                request.ClientId,
                request.ProCursorSourceScopeMode,
                request.ProCursorSourceIds,
                ct);

            if (await crawlConfigRepo.ExistsAsync(
                    request.ClientId,
                    resolvedOrganization.OrganizationUrl,
                    request.ProviderProjectKey.Trim(),
                    null,
                    null,
                    ct))
            {
                return this.Conflict(new { error = "A crawl configuration for this organisation and project already exists." });
            }

            var config = await crawlConfigRepo.AddAsync(
                request.ClientId,
                request.Provider,
                resolvedOrganization.OrganizationUrl,
                request.ProviderProjectKey.Trim(),
                request.CrawlIntervalSeconds,
                resolvedOrganization.OrganizationScopeId,
                ct);

            if (repoFilters.Count > 0)
            {
                await crawlConfigRepo.UpdateRepoFiltersAsync(config.Id, repoFilters, ct);
            }

            if (request.ProCursorSourceScopeMode != ProCursorSourceScopeMode.AllClientSources ||
                selectedProCursorSourceIds.Count > 0)
            {
                await crawlConfigRepo.UpdateSourceScopeAsync(
                    config.Id,
                    request.ProCursorSourceScopeMode,
                    selectedProCursorSourceIds,
                    ct);
            }

            var refreshed = await crawlConfigRepo.GetByIdAsync(config.Id, ct);
            if (refreshed is null)
            {
                return this.NotFound();
            }

            LogCrawlConfigCreated(logger, refreshed.Id, refreshed.ClientId);

            return this.CreatedAtAction(
                nameof(this.GetCrawlConfigurations),
                null,
                ToCrawlConfigResponse(refreshed));
        }
        catch (InvalidOperationException ex)
        {
            LogGuidedCrawlConfigCreateValidationFailed(
                logger,
                request.ClientId,
                request.ProviderProjectKey.Trim(),
                request.OrganizationScopeId,
                ex.Message);
            return this.Conflict(new { error = ex.Message });
        }
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

        var capability = await this.RequireCrawlConfigsCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var existing = await crawlConfigRepo.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
            // Non-Admin: require ClientAdministrator role for the config's client
            var roleCheck = AuthHelpers.RequireClientRole(
                this.HttpContext,
                existing.ClientId,
                ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
            }
        }

        try
        {
            Guid? ownerScope = isAdmin ? null : existing.ClientId;
            var updated = await crawlConfigRepo.UpdateAsync(
                configId,
                request.CrawlIntervalSeconds,
                request.IsActive,
                ownerScope,
                ct);

            if (!updated)
            {
                return this.NotFound();
            }

            // Update repo filters when explicitly provided (omitting the field = leave unchanged).
            if (request.RepoFilters is not null)
            {
                var filterDtos = await this.ResolveRepoFiltersAsync(
                    existing.ClientId,
                    existing.Provider,
                    existing.OrganizationScopeId,
                    existing.ProviderProjectKey,
                    request.RepoFilters,
                    ct);
                await crawlConfigRepo.UpdateRepoFiltersAsync(configId, filterDtos, ct);
            }

            if (request.ProCursorSourceScopeMode.HasValue || request.ProCursorSourceIds is not null)
            {
                var effectiveScopeMode = request.ProCursorSourceScopeMode
                                         ?? (request.ProCursorSourceIds is not null
                                             ? ProCursorSourceScopeMode.SelectedSources
                                             : existing.ProCursorSourceScopeMode);
                var validatedSourceIds = await this.ValidateSelectedProCursorSourcesAsync(
                    existing.ClientId,
                    effectiveScopeMode,
                    request.ProCursorSourceIds ?? existing.ProCursorSourceIds,
                    ct);

                await crawlConfigRepo.UpdateSourceScopeAsync(
                    configId,
                    effectiveScopeMode,
                    validatedSourceIds,
                    ct);
            }

            var refreshed = await crawlConfigRepo.GetByIdAsync(configId, ct);
            if (refreshed is null)
            {
                return this.NotFound();
            }

            return this.Ok(ToCrawlConfigResponse(refreshed));
        }
        catch (InvalidOperationException ex)
        {
            LogGuidedCrawlConfigPatchValidationFailed(logger, configId, ex.Message);
            return this.Conflict(new { error = ex.Message });
        }
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

        var capability = await this.RequireCrawlConfigsCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var existing = await crawlConfigRepo.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
            // Non-Admin: require ClientAdministrator role for the config's client
            var roleCheck = AuthHelpers.RequireClientRole(
                this.HttpContext,
                existing.ClientId,
                ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
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
    string ProviderProjectKey,
    ScmProvider Provider = ScmProvider.AzureDevOps,
    Guid? OrganizationScopeId = null,
    string? ProviderScopePath = null,
    int CrawlIntervalSeconds = 60,
    IReadOnlyList<CrawlRepoFilterRequest>? RepoFilters = null,
    ProCursorSourceScopeMode ProCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
    IReadOnlyList<Guid>? ProCursorSourceIds = null);

/// <summary>
///     Request body for patching an admin-managed crawl configuration.
///     Omit a field to leave it unchanged.
/// </summary>
public sealed record PatchAdminCrawlConfigRequest(
    int? CrawlIntervalSeconds = null,
    bool? IsActive = null,
    IReadOnlyList<CrawlRepoFilterRequest>? RepoFilters = null,
    ProCursorSourceScopeMode? ProCursorSourceScopeMode = null,
    IReadOnlyList<Guid>? ProCursorSourceIds = null);

/// <summary>A single repo filter entry in a PATCH request.</summary>
public sealed record CrawlRepoFilterRequest(
    string? RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);
