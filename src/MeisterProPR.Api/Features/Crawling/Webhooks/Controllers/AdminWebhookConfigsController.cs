// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoints for managing webhook configurations and recent delivery history.</summary>
[ApiController]
public sealed partial class AdminWebhookConfigsController(
    IWebhookConfigurationRepository webhookConfigurationRepository,
    IWebhookDeliveryLogRepository webhookDeliveryLogRepository,
    IUserRepository userRepository,
    IClientAdminService clientAdminService,
    IScmProviderRegistry providerRegistry,
    IWebhookSecretGenerator webhookSecretGenerator,
    ISecretProtectionCodec secretProtectionCodec,
    IConfiguration configuration,
    ILogger<AdminWebhookConfigsController> logger,
    IProviderActivationService? providerActivationService = null) : ControllerBase
{
    private const int MaxDeliveryHistoryTake = 200;

    private const string WebhookConfigurationConflictMessage =
        "The selected webhook configuration is no longer valid. Refresh the provider selections and try again.";

    private const string DisabledProviderMessage =
        "The selected provider family is currently disabled by system administration.";

    private static IActionResult? Validate(ControllerBase controller, ValidationResult result)
    {
        if (result.IsValid)
        {
            return null;
        }

        foreach (var error in result.Errors)
        {
            controller.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return controller.ValidationProblem();
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

    private async Task<bool> IsProviderEnabledAsync(WebhookProviderType provider, CancellationToken ct)
    {
        if (providerActivationService is null)
        {
            return true;
        }

        var scmProvider = provider switch
        {
            WebhookProviderType.AzureDevOps => ScmProvider.AzureDevOps,
            WebhookProviderType.GitHub => ScmProvider.GitHub,
            WebhookProviderType.GitLab => ScmProvider.GitLab,
            WebhookProviderType.Forgejo => ScmProvider.Forgejo,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };

        return await providerActivationService.IsEnabledAsync(scmProvider, ct);
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

    private static string ResolveRepositoryName(WebhookRepoFilterRequest filter)
    {
        var repositoryName = NormalizeOptional(filter.RepositoryName)
                             ?? NormalizeOptional(filter.DisplayName)
                             ?? NormalizeOptional(filter.CanonicalSourceRef?.Value);

        return repositoryName
               ?? throw new InvalidOperationException("Unable to resolve a repository identifier from the provided filter properties.");
    }

    private static WebhookRepoFilterResponse ToWebhookRepoFilterResponse(WebhookRepoFilterDto filter)
    {
        return new WebhookRepoFilterResponse(
            filter.Id,
            filter.RepositoryName,
            filter.TargetBranchPatterns,
            filter.CanonicalSourceRef,
            filter.DisplayName);
    }

    private WebhookConfigurationResponse ToWebhookConfigurationResponse(
        WebhookConfigurationDto config,
        string? generatedSecret = null)
    {
        return new WebhookConfigurationResponse(
            config.Id,
            config.ClientId,
            config.ProviderType,
            config.OrganizationScopeId,
            config.OrganizationUrl,
            config.ProjectId,
            config.IsActive,
            config.EnabledEvents,
            config.RepoFilters.Select(ToWebhookRepoFilterResponse).ToList().AsReadOnly(),
            this.BuildListenerUrl(config.ProviderType, config.PublicPathKey),
            generatedSecret,
            config.CreatedAt);
    }

    private static WebhookDeliveryLogEntryResponse ToWebhookDeliveryLogEntryResponse(WebhookDeliveryLogEntryDto entry)
    {
        return new WebhookDeliveryLogEntryResponse(
            entry.Id,
            entry.WebhookConfigurationId,
            entry.ReceivedAt,
            entry.EventType,
            entry.DeliveryOutcome,
            entry.HttpStatusCode,
            entry.RepositoryId,
            entry.PullRequestId,
            entry.SourceBranch,
            entry.TargetBranch,
            entry.ActionSummaries,
            entry.FailureReason,
            entry.FailureCategory);
    }

    private string BuildListenerUrl(WebhookProviderType provider, string pathKey)
    {
        var providerSegment = GetProviderPathSegment(provider);
        var listenerPath = $"/webhooks/v1/providers/{providerSegment}/{pathKey}";
        var publicBaseUrl = configuration["MEISTER_PUBLIC_BASE_URL"];

        if (Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicBaseUri))
        {
            var normalizedBaseUrl = publicBaseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? publicBaseUri.AbsoluteUri
                : publicBaseUri.AbsoluteUri + "/";

            return new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), listenerPath.TrimStart('/')).ToString();
        }

        var pathBase = this.Request.PathBase.HasValue ? this.Request.PathBase.Value : string.Empty;
        if (string.IsNullOrWhiteSpace(this.Request.Scheme) || !this.Request.Host.HasValue)
        {
            return $"{pathBase}{listenerPath}";
        }

        return $"{this.Request.Scheme}://{this.Request.Host}{pathBase}{listenerPath}";
    }

    private async Task<(Guid? OrganizationScopeId, string OrganizationUrl)> ResolveOrganizationSelectionAsync(
        Guid clientId,
        WebhookProviderType provider,
        Guid? organizationScopeId,
        string? organizationUrl,
        CancellationToken ct)
    {
        if (provider != WebhookProviderType.AzureDevOps)
        {
            var normalizedProviderOrganizationUrl = NormalizeOptional(organizationUrl)
                                                    ?? throw new InvalidOperationException(
                                                        "ProviderScopePath is required for non-Azure DevOps webhook configurations.");

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

    private async Task<IReadOnlyList<WebhookRepoFilterDto>> ResolveRepoFiltersAsync(
        Guid clientId,
        WebhookProviderType provider,
        Guid? organizationScopeId,
        string projectId,
        IReadOnlyList<WebhookRepoFilterRequest>? repoFilters,
        CancellationToken ct)
    {
        if (repoFilters is null)
        {
            return [];
        }

        var filterDtos = new List<WebhookRepoFilterDto>(repoFilters.Count);
        IReadOnlyList<AdoCrawlFilterOptionDto> availableFilters = [];

        if (provider == WebhookProviderType.AzureDevOps && organizationScopeId.HasValue)
        {
            availableFilters = await providerRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                .ListCrawlFiltersAsync(clientId, organizationScopeId.Value, projectId, ct);
        }

        foreach (var filter in repoFilters)
        {
            var normalizedProvider = NormalizeOptional(filter.CanonicalSourceRef?.Provider);
            var normalizedValue = NormalizeOptional(filter.CanonicalSourceRef?.Value);
            var normalizedDisplayName = NormalizeOptional(filter.DisplayName);
            var repositoryName = ResolveRepositoryName(filter);
            var targetBranchPatterns = NormalizeBranchPatterns(filter.TargetBranchPatterns);

            if (provider == WebhookProviderType.AzureDevOps && organizationScopeId.HasValue &&
                normalizedProvider is not null && normalizedValue is not null)
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
                        $"The selected webhook filter '{normalizedDisplayName ?? repositoryName}' is no longer available in Azure DevOps.");
                }

                filterDtos.Add(
                    new WebhookRepoFilterDto(
                        Guid.Empty,
                        repositoryName,
                        targetBranchPatterns,
                        new CanonicalSourceReferenceDto(normalizedProvider, normalizedValue),
                        normalizedDisplayName ?? matchedFilter.DisplayName));
                continue;
            }

            filterDtos.Add(
                new WebhookRepoFilterDto(
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

    private static string GetProviderPathSegment(WebhookProviderType provider)
    {
        return provider switch
        {
            WebhookProviderType.AzureDevOps => "ado",
            WebhookProviderType.GitHub => "github",
            WebhookProviderType.GitLab => "gitlab",
            WebhookProviderType.Forgejo => "forgejo",
            _ => provider.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Lists all webhook configurations visible to the caller.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of webhook configurations.</response>
    /// <response code="401">No valid credentials provided.</response>
    [HttpGet("/admin/webhook-configurations")]
    [ProducesResponseType(typeof(IReadOnlyList<WebhookConfigurationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWebhookConfigurations(CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out var userId);
        if (auth is not null)
        {
            return auth;
        }

        IReadOnlyList<WebhookConfigurationDto> configs;
        if (isAdmin)
        {
            configs = await webhookConfigurationRepository.GetAllAsync(ct);
        }
        else
        {
            var user = await userRepository.GetByIdWithAssignmentsAsync(userId!.Value, ct);
            var clientIds = user?.ClientAssignments.Select(assignment => assignment.ClientId).ToList() ?? [];
            configs = clientIds.Count > 0
                ? await webhookConfigurationRepository.GetByClientIdsAsync(clientIds, ct)
                : [];
        }

        return this.Ok(configs.Select(config => this.ToWebhookConfigurationResponse(config)));
    }

    /// <summary>Lists recent delivery-log entries for one webhook configuration.</summary>
    /// <param name="configId">Webhook configuration identifier.</param>
    /// <param name="take">Maximum number of entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Recent delivery-log entries.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Caller lacks access to the target client.</response>
    /// <response code="404">Configuration not found.</response>
    [HttpGet("/admin/webhook-configurations/{configId:guid}/deliveries")]
    [ProducesResponseType(typeof(WebhookDeliveryHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWebhookConfigurationDeliveries(
        Guid configId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var existing = await webhookConfigurationRepository.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
            var roleCheck = AuthHelpers.RequireClientRole(
                this.HttpContext,
                existing.ClientId,
                ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
            }
        }

        take = Math.Clamp(take, 1, MaxDeliveryHistoryTake);
        var entries = await webhookDeliveryLogRepository.ListByWebhookConfigurationAsync(configId, take, ct);
        return this.Ok(new WebhookDeliveryHistoryResponse(entries.Select(ToWebhookDeliveryLogEntryResponse).ToList().AsReadOnly()));
    }

    /// <summary>Creates a new webhook configuration and returns the one-time secret.</summary>
    /// <param name="request">Webhook configuration details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Configuration created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Caller lacks access to the target client.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">Selected guided scope is no longer valid.</response>
    [HttpPost("/admin/webhook-configurations")]
    [ProducesResponseType(typeof(WebhookConfigurationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateWebhookConfiguration(
        [FromBody] CreateAdminWebhookConfigRequest request,
        [FromServices] IValidator<CreateAdminWebhookConfigRequest> validator,
        CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var validation = Validate(this, await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (!isAdmin)
        {
            var roleCheck = AuthHelpers.RequireClientRole(
                this.HttpContext,
                request.ClientId,
                ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
            }
        }
        else if (!await clientAdminService.ExistsAsync(request.ClientId, ct))
        {
            return this.NotFound();
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

            var generatedSecret = webhookSecretGenerator.GenerateSecret();
            var protectedSecret = secretProtectionCodec.Protect(generatedSecret, "WebhookSecret");
            var pathKey = Guid.NewGuid().ToString("N");

            var created = await webhookConfigurationRepository.AddAsync(
                request.ClientId,
                request.Provider,
                pathKey,
                resolvedOrganization.OrganizationUrl,
                request.ProviderProjectKey.Trim(),
                protectedSecret,
                request.EnabledEvents ?? [],
                resolvedOrganization.OrganizationScopeId,
                ct);

            if (repoFilters.Count > 0)
            {
                await webhookConfigurationRepository.UpdateRepoFiltersAsync(created.Id, repoFilters, ct);
            }

            var refreshed = await webhookConfigurationRepository.GetByIdAsync(created.Id, ct);
            if (refreshed is null)
            {
                return this.NotFound();
            }

            LogWebhookConfigCreated(logger, refreshed.Id, refreshed.ClientId);

            return this.CreatedAtAction(
                nameof(this.GetWebhookConfigurations),
                null,
                this.ToWebhookConfigurationResponse(refreshed, generatedSecret));
        }
        catch (InvalidOperationException ex)
        {
            LogWebhookConfigCreateValidationFailed(
                logger,
                request.ClientId,
                request.ProviderProjectKey.Trim(),
                request.OrganizationScopeId,
                ex.Message);
            return this.Conflict(new { error = WebhookConfigurationConflictMessage });
        }
    }

    /// <summary>Applies partial updates to a webhook configuration.</summary>
    /// <param name="configId">Webhook configuration identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Configuration updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Caller lacks access to the target client.</response>
    /// <response code="404">Configuration not found.</response>
    /// <response code="409">Selected guided scope is no longer valid.</response>
    [HttpPatch("/admin/webhook-configurations/{configId:guid}")]
    [ProducesResponseType(typeof(WebhookConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PatchWebhookConfiguration(
        Guid configId,
        [FromBody] PatchAdminWebhookConfigRequest request,
        [FromServices] IValidator<PatchAdminWebhookConfigRequest> validator,
        CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var validation = Validate(this, await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var existing = await webhookConfigurationRepository.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
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
            var updated = await webhookConfigurationRepository.UpdateAsync(
                configId,
                request.IsActive,
                request.EnabledEvents,
                isAdmin ? null : existing.ClientId,
                ct);

            if (!updated)
            {
                return this.NotFound();
            }

            if (request.RepoFilters is not null)
            {
                var repoFilters = await this.ResolveRepoFiltersAsync(
                    existing.ClientId,
                    existing.ProviderType,
                    existing.OrganizationScopeId,
                    existing.ProjectId,
                    request.RepoFilters,
                    ct);
                await webhookConfigurationRepository.UpdateRepoFiltersAsync(configId, repoFilters, ct);
            }

            var refreshed = await webhookConfigurationRepository.GetByIdAsync(configId, ct);
            if (refreshed is null)
            {
                return this.NotFound();
            }

            return this.Ok(this.ToWebhookConfigurationResponse(refreshed));
        }
        catch (InvalidOperationException ex)
        {
            LogWebhookConfigPatchValidationFailed(logger, configId, ex.Message);
            return this.Conflict(new { error = WebhookConfigurationConflictMessage });
        }
    }

    /// <summary>Deletes a webhook configuration.</summary>
    /// <param name="configId">Webhook configuration identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Configuration deleted.</response>
    /// <response code="401">No valid credentials provided.</response>
    /// <response code="403">Caller lacks access to the target client.</response>
    /// <response code="404">Configuration not found.</response>
    [HttpDelete("/admin/webhook-configurations/{configId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWebhookConfiguration(Guid configId, CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var existing = await webhookConfigurationRepository.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        if (!isAdmin)
        {
            var roleCheck = AuthHelpers.RequireClientRole(
                this.HttpContext,
                existing.ClientId,
                ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
            }
        }

        await webhookConfigurationRepository.DeleteAsync(configId, existing.ClientId, ct);
        LogWebhookConfigDeleted(logger, configId);
        return this.NoContent();
    }

    [LoggerMessage(
        EventId = 2812,
        Level = LogLevel.Information,
        Message = "Created webhook configuration {ConfigurationId} for client {ClientId}.")]
    private static partial void LogWebhookConfigCreated(ILogger logger, Guid configurationId, Guid clientId);

    [LoggerMessage(
        EventId = 2813,
        Level = LogLevel.Information,
        Message = "Deleted webhook configuration {ConfigurationId}.")]
    private static partial void LogWebhookConfigDeleted(ILogger logger, Guid configurationId);

    [LoggerMessage(
        EventId = 2814,
        Level = LogLevel.Warning,
        Message =
            "Webhook configuration create validation failed for client {ClientId}, provider project {ProjectId}, scope {OrganizationScopeId}: {Reason}.")]
    private static partial void LogWebhookConfigCreateValidationFailed(
        ILogger logger,
        Guid clientId,
        string projectId,
        Guid? organizationScopeId,
        string reason);

    [LoggerMessage(
        EventId = 2815,
        Level = LogLevel.Warning,
        Message = "Webhook configuration patch validation failed for config {ConfigurationId}: {Reason}.")]
    private static partial void LogWebhookConfigPatchValidationFailed(
        ILogger logger,
        Guid configurationId,
        string reason);
}

/// <summary>Request body for creating an admin-managed webhook configuration.</summary>
public sealed record CreateAdminWebhookConfigRequest(
    Guid ClientId,
    WebhookProviderType Provider = WebhookProviderType.AzureDevOps,
    Guid? OrganizationScopeId = null,
    string? ProviderScopePath = null,
    string ProviderProjectKey = "",
    IReadOnlyList<WebhookEventType>? EnabledEvents = null,
    IReadOnlyList<WebhookRepoFilterRequest>? RepoFilters = null);

/// <summary>Request body for patching an admin-managed webhook configuration.</summary>
public sealed record PatchAdminWebhookConfigRequest(
    bool? IsActive = null,
    IReadOnlyList<WebhookEventType>? EnabledEvents = null,
    IReadOnlyList<WebhookRepoFilterRequest>? RepoFilters = null);

/// <summary>A single repo filter entry in a webhook create or patch request.</summary>
public sealed record WebhookRepoFilterRequest(
    string? RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);

/// <summary>Response payload for one webhook configuration.</summary>
public sealed record WebhookConfigurationResponse(
    Guid Id,
    Guid ClientId,
    WebhookProviderType Provider,
    Guid? OrganizationScopeId,
    string ProviderScopePath,
    string ProviderProjectKey,
    bool IsActive,
    IReadOnlyList<WebhookEventType> EnabledEvents,
    IReadOnlyList<WebhookRepoFilterResponse> RepoFilters,
    string ListenerUrl,
    string? GeneratedSecret,
    DateTimeOffset CreatedAt);

/// <summary>Response payload for one webhook repository filter.</summary>
public sealed record WebhookRepoFilterResponse(
    Guid Id,
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);

/// <summary>Response payload for recent delivery-history entries.</summary>
public sealed record WebhookDeliveryHistoryResponse(IReadOnlyList<WebhookDeliveryLogEntryResponse> Items);

/// <summary>Response payload for one delivery-history entry.</summary>
public sealed record WebhookDeliveryLogEntryResponse(
    Guid Id,
    Guid WebhookConfigurationId,
    DateTimeOffset ReceivedAt,
    string EventType,
    WebhookDeliveryOutcome DeliveryOutcome,
    int HttpStatusCode,
    string? RepositoryId,
    int? PullRequestId,
    string? SourceBranch,
    string? TargetBranch,
    IReadOnlyList<string> ActionSummaries,
    string? FailureReason,
    string? FailureCategory = null);
