// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Admin endpoints for managing which repositories a client answers `@`-mentions on.</summary>
/// <remarks>
///     Two clients may claim the same repository and nothing here objects. A client administrator cannot
///     see another client's configuration, so refusing would be an error they could neither understand nor
///     resolve, and naming the other holder would disclose another tenant's setup. One answer per question
///     is kept by the mention job's uniqueness rule instead, where the check can see across clients without
///     telling anyone what it saw.
/// </remarks>
[ApiController]
[Route("admin/mention-configurations")]
public sealed partial class AdminMentionConfigsController(
    IMentionConfigurationRepository mentionConfigRepo,
    IUserRepository userRepository,
    IClientAdminService clientAdminService,
    IClientAdoOrganizationScopeRepository organizationScopeRepository,
    ILogger<AdminMentionConfigsController> logger,
    IProviderActivationService? providerActivationService = null) : ControllerBase
{
    private const string DisabledProviderMessage =
        "The selected provider family is currently disabled by system administration.";

    private const string NoRepositoriesMessage =
        "A mention configuration must name at least one repository.";

    /// <summary>Lists mention configurations visible to the caller.</summary>
    /// <param name="clientId">Optional client filter. Non-admin callers are restricted to their own clients regardless.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The configurations the caller may see.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the requested client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MentionConfigResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] Guid? clientId, CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out var userId);
        if (auth is not null)
        {
            return auth;
        }

        if (clientId is { } requested)
        {
            if (!isAdmin
                && AuthHelpers.RequireClientRole(this.HttpContext, requested, ClientRole.ClientAdministrator)
                    is { } roleCheck)
            {
                return roleCheck;
            }

            var forClient = await mentionConfigRepo.GetByClientAsync(requested, ct);
            return this.Ok(forClient.Select(ToResponse).ToList());
        }

        if (isAdmin)
        {
            // Every configuration, not only the active ones. A paused configuration that no listing shows
            // cannot be reactivated, and the uniqueness rule refuses to replace it.
            var all = await mentionConfigRepo.GetAllAsync(ct);
            return this.Ok(all.Select(ToResponse).ToList());
        }

        // An authenticated caller with no resolvable user identity has no assignments to read, so there is
        // nothing they may see. Answered as an empty listing rather than a fault, because asking is legitimate.
        if (userId is not { } callerId)
        {
            return this.Ok(new List<MentionConfigResponse>());
        }

        // A caller without the admin role sees exactly the clients they administer, so the listing is
        // assembled from their assignments rather than filtered after the fact. The role is checked per
        // assignment for the same reason the filtered branch checks it: asking broadly must not return what
        // asking narrowly would refuse.
        var user = await userRepository.GetByIdWithAssignmentsAsync(callerId, ct);
        var visible = new List<MentionConfigResponse>();
        foreach (var assignment in user?.ClientAssignments ?? [])
        {
            if (AuthHelpers.RequireClientRole(this.HttpContext, assignment.ClientId, ClientRole.ClientAdministrator)
                is not null)
            {
                continue;
            }

            var configs = await mentionConfigRepo.GetByClientAsync(assignment.ClientId, ct);
            visible.AddRange(configs.Select(ToResponse));
        }

        return this.Ok(visible);
    }

    /// <summary>Declares that a client answers mentions on a set of repositories in one project.</summary>
    /// <param name="request">The configuration to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The created configuration.</response>
    /// <response code="400">No repositories were named, or the provider family is disabled.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="404">No such client.</response>
    /// <response code="409">The client already has a configuration for this project.</response>
    [HttpPost]
    [ProducesResponseType(typeof(MentionConfigResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateMentionConfigRequest request,
        [FromServices] IValidator<CreateMentionConfigRequest> validator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validator);

        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.Validate(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var authorization = await this.AuthorizeWriteAsync(isAdmin, request.ClientId, ct);
        if (authorization is not null)
        {
            return authorization;
        }

        if (!await this.IsProviderEnabledAsync(request.Provider, ct))
        {
            return this.BadRequest(new { error = DisabledProviderMessage });
        }

        var filters = NormalizeFilters(request.RepoFilters);
        if (filters.Count == 0)
        {
            return this.BadRequest(new { error = NoRepositoriesMessage });
        }

        var scopePath = NormalizeScopePath(request.ProviderScopePath);
        var projectKey = request.ProviderProjectKey?.Trim() ?? string.Empty;

        if (request.Provider == ScmProvider.AzureDevOps && !await this.IsKnownOrganizationAsync(request.ClientId, scopePath, ct))
        {
            return this.BadRequest(
                new
                {
                    error = "That Azure DevOps organization is not configured for this client. Add and enable it first.",
                });
        }

        var existing = await mentionConfigRepo.GetByClientAsync(request.ClientId, ct);
        if (existing.Any(c => c.Provider == request.Provider
                              && string.Equals(c.ProviderScopePath, scopePath, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(c.ProviderProjectKey, projectKey, StringComparison.OrdinalIgnoreCase)))
        {
            return this.Conflict(
                new
                {
                    error = "This client already answers mentions in that project. Edit that configuration instead.",
                });
        }

        MentionConfigurationDto created;
        try
        {
            created = await mentionConfigRepo.AddAsync(
                request.ClientId,
                request.Provider,
                scopePath,
                projectKey,
                request.ScanIntervalSeconds ?? 60,
                filters,
                ct);
        }
        catch (DbUpdateException exception) when (IsDuplicateProject(exception))
        {
            // The check above reads before it writes, so two saves arriving together both pass it. The
            // database settles it, and the loser is told the same thing the check would have told them
            // rather than being handed a fault.
            LogMentionConfigConflict(logger, request.ClientId);
            return this.Conflict(
                new
                {
                    error = "This client already answers mentions in that project. Edit that configuration instead.",
                });
        }

        LogMentionConfigCreated(logger, created.Id, created.ClientId, filters.Count);
        return this.CreatedAtAction(nameof(this.List), new { clientId = created.ClientId }, ToResponse(created));
    }

    /// <summary>Changes a configuration's scan interval, active flag, or repository list.</summary>
    /// <param name="configId">The configuration to change.</param>
    /// <param name="request">The changes to apply. Omitted fields are left as they are.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The updated configuration.</response>
    /// <response code="400">The repository list was given but empty.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="404">No such configuration.</response>
    [HttpPatch("{configId:guid}")]
    [ProducesResponseType(typeof(MentionConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Patch(
        Guid configId,
        [FromBody] PatchMentionConfigRequest request,
        [FromServices] IValidator<PatchMentionConfigRequest> validator,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validator);

        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.Validate(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var existing = await mentionConfigRepo.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        var authorization = await this.AuthorizeWriteAsync(isAdmin, existing.ClientId, ct);
        if (authorization is not null)
        {
            return authorization;
        }

        IReadOnlyList<MentionRepoFilterDto>? filters = null;
        if (request.RepoFilters is not null)
        {
            filters = NormalizeFilters(request.RepoFilters);
            if (filters.Count == 0)
            {
                return this.BadRequest(new { error = NoRepositoriesMessage });
            }
        }

        var updated = await mentionConfigRepo.UpdateAsync(
            configId,
            existing.ClientId,
            request.ScanIntervalSeconds,
            request.IsActive,
            filters,
            ct);

        if (!updated)
        {
            return this.NotFound();
        }

        LogMentionConfigUpdated(logger, configId, existing.ClientId);
        var reloaded = await mentionConfigRepo.GetByIdAsync(configId, ct);
        return reloaded is null ? this.NotFound() : this.Ok(ToResponse(reloaded));
    }

    /// <summary>Removes a configuration, so the client stops answering mentions in that project.</summary>
    /// <param name="configId">The configuration to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Removed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="404">No such configuration.</response>
    [HttpDelete("{configId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid configId, CancellationToken ct = default)
    {
        var auth = this.RequireAuth(out var isAdmin, out _);
        if (auth is not null)
        {
            return auth;
        }

        var existing = await mentionConfigRepo.GetByIdAsync(configId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        var authorization = await this.AuthorizeWriteAsync(isAdmin, existing.ClientId, ct);
        if (authorization is not null)
        {
            return authorization;
        }

        var deleted = await mentionConfigRepo.DeleteAsync(configId, existing.ClientId, ct);
        if (!deleted)
        {
            return this.NotFound();
        }

        LogMentionConfigDeleted(logger, configId, existing.ClientId);
        return this.NoContent();
    }

    private static MentionConfigResponse ToResponse(MentionConfigurationDto config)
    {
        return new MentionConfigResponse(
            config.Id,
            config.ClientId,
            config.Provider,
            config.ProviderScopePath,
            config.ProviderProjectKey,
            config.ScanIntervalSeconds,
            config.IsActive,
            config.CreatedAt,
            config.RepoFilters
                .Select(filter => new MentionRepoFilterResponse(
                    filter.Id,
                    filter.RepositoryId,
                    filter.DisplayName,
                    filter.CanonicalSourceRef,
                    filter.SourceProvider))
                .ToList());
    }

    // A repository named twice is one repository. Trimming and de-duplicating here rather than at the
    // database keeps the caller from seeing a unique-constraint error for something the screen could have
    // sent by accident.
    private static IReadOnlyList<MentionRepoFilterDto> NormalizeFilters(IReadOnlyList<MentionRepoFilterRequest>? requested)
    {
        return (requested ?? [])
            .Where(filter => filter is not null)
            .Select(filter => new
            {
                RepositoryId = filter.RepositoryId?.Trim(),
                filter.CanonicalSourceRef,
                filter.DisplayName,
                filter.SourceProvider,
            })
            .Where(filter => !string.IsNullOrWhiteSpace(filter.RepositoryId))
            .DistinctBy(filter => filter.RepositoryId, StringComparer.OrdinalIgnoreCase)
            .Select(filter => new MentionRepoFilterDto(
                Guid.Empty,
                filter.RepositoryId!,
                filter.CanonicalSourceRef,
                filter.DisplayName,
                filter.SourceProvider))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Strips the trailing separator and surrounding whitespace from a provider scope path.
    /// </summary>
    /// <remarks>
    ///     Two spellings of one organization differing only by a trailing slash are the same project to
    ///     every provider, and would otherwise be two configurations scanning it twice and billing twice.
    ///     Only the separator is touched: the rest of the path is the operator's, and casing is left to the
    ///     case-insensitive comparison and the database's own rule.
    /// </remarks>
    private static string NormalizeScopePath(string? scopePath)
    {
        return string.IsNullOrWhiteSpace(scopePath) ? string.Empty : scopePath.Trim().TrimEnd('/');
    }

    /// <summary>Reports whether a scope path names an Azure DevOps organization this client has enabled.</summary>
    /// <remarks>
    ///     A scope path that matches no configured organization has no credential behind it, and the runtime
    ///     answers an absent credential by falling back to the platform's own identity. Storing such a path
    ///     would therefore point a scan, carrying that identity, at whatever host was typed. Requiring the
    ///     organization to be one the client has already set up is the same rule the form applies, enforced
    ///     where a request that skipped the form still has to pass.
    /// </remarks>
    private async Task<bool> IsKnownOrganizationAsync(Guid clientId, string scopePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scopePath))
        {
            return false;
        }

        var scopes = await organizationScopeRepository.GetByClientIdAsync(clientId, ct);
        return scopes.Any(scope =>
            scope.IsEnabled &&
            string.Equals(NormalizeScopePath(scope.OrganizationUrl), scopePath, StringComparison.OrdinalIgnoreCase));
    }

    // Narrowed to the one constraint that means "this client already covers this project". Treating every
    // write failure as a conflict would report a storage fault as an operator mistake.
    private static bool IsDuplicateProject(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
               && string.Equals(
                   postgres.ConstraintName,
                   "uq_mention_configurations_client_project",
                   StringComparison.Ordinal);
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

        return this.ValidationProblem(this.ModelState);
    }

    private IActionResult? RequireAuth(out bool isAdmin, out Guid? userId)
    {
        isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        userId = AuthHelpers.GetUserId(this.HttpContext);
        return AuthHelpers.RequireAuthenticated(this.HttpContext);
    }

    private async Task<IActionResult?> AuthorizeWriteAsync(bool isAdmin, Guid clientId, CancellationToken ct)
    {
        if (!isAdmin)
        {
            return AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        }

        return await clientAdminService.ExistsAsync(clientId, ct) ? null : this.NotFound();
    }

    private async Task<bool> IsProviderEnabledAsync(ScmProvider provider, CancellationToken ct)
    {
        return providerActivationService is null || await providerActivationService.IsEnabledAsync(provider, ct);
    }
}
