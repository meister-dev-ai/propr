using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages clients (admin) and crawl configurations (client-scoped).</summary>
[ApiController]
public sealed partial class ClientsController(
    IClientAdminService clientAdminService,
    IClientRegistry clientRegistry,
    ICrawlConfigurationRepository crawlConfigs,
    IClientAdoCredentialRepository adoCredentialRepository,
    ILogger<ClientsController> logger) : ControllerBase
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reviewer identity updated for client {ClientId} by {ActorType}")]
    private static partial void LogReviewerIdentityUpdated(ILogger logger, Guid clientId, string actorType);

    private static ClientResponse ToClientResponse(ClientDto client)
    {
        return new ClientResponse(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.HasAdoCredentials,
            client.ReviewerId,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage);
    }

    private IActionResult? ValidateRequest(ValidationResult result)
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

    /// <summary>
    ///     Adds a crawl configuration for the specified client. Requires <c>X-Client-Key</c> that owns the client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Crawl configuration details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Configuration created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">A crawl configuration for this organisation and project already exists.</response>
    [HttpPost("clients/{clientId:guid}/crawl-configurations")]
    [ProducesResponseType(typeof(CrawlConfigResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddCrawlConfiguration(
        Guid clientId,
        [FromBody] CreateCrawlConfigRequest request,
        [FromServices] IValidator<CreateCrawlConfigRequest> validator,
        CancellationToken ct = default)
    {
        var callerKey = this.HttpContext.Items["ClientKey"] as string;
        if (string.IsNullOrWhiteSpace(callerKey))
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var callerId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
        if (callerId is null || callerId != clientId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller does not own this client." });
        }

        var clientExists = await clientAdminService.ExistsAsync(clientId, ct);
        if (!clientExists)
        {
            return this.NotFound();
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (await crawlConfigs.ExistsAsync(clientId, request.OrganizationUrl, request.ProjectId, ct))
        {
            return this.Conflict(new { error = "A crawl configuration for this organisation and project already exists." });
        }

        var config = await crawlConfigs.AddAsync(
            clientId,
            request.OrganizationUrl,
            request.ProjectId,
            request.CrawlIntervalSeconds,
            ct);

        return this.CreatedAtAction(
            nameof(this.GetCrawlConfigurations),
            new { clientId },
            new CrawlConfigResponse(
                config.Id,
                config.ClientId,
                config.OrganizationUrl,
                config.ProjectId,
                config.CrawlIntervalSeconds,
                config.IsActive,
                config.CreatedAt));
    }

    /// <summary>
    ///     Registers a new client. Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="request">Client registration details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Client registered.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="409">A client with that key already exists.</response>
    [HttpPost("clients")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateClient(
        [FromBody] CreateClientRequest request,
        [FromServices] IValidator<CreateClientRequest> validator,
        CancellationToken ct = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var client = await clientAdminService.CreateAsync(request.Key, request.DisplayName, ct);
        if (client is null)
        {
            return this.Conflict(new { error = "A client with that key already exists." });
        }

        return this.CreatedAtAction(
            nameof(this.GetClient),
            new { clientId = client.Id },
            ToClientResponse(client));
    }

    /// <summary>
    ///     Removes ADO service principal credentials from a client. Requires <c>X-Admin-Key</c>.
    ///     The client falls back to the global backend identity on subsequent ADO operations.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Credentials removed (or client had no credentials — idempotent).</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpDelete("clients/{clientId:guid}/ado-credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAdoCredentials(Guid clientId, CancellationToken ct = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        await adoCredentialRepository.ClearAsync(clientId, ct);
        return this.NoContent();
    }

    /// <summary>
    ///     Deletes a client and all its crawl configurations. Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <response code="204">Client deleted.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpDelete("clients/{clientId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var deleted = await clientAdminService.DeleteAsync(clientId);
        return deleted ? this.NoContent() : this.NotFound();
    }

    /// <summary>
    ///     Deletes a crawl configuration. Requires <c>X-Client-Key</c> that owns the client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="configId">Configuration identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Configuration deleted.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">Configuration not found.</response>
    [HttpDelete("clients/{clientId:guid}/crawl-configurations/{configId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCrawlConfiguration(
        Guid clientId,
        Guid configId,
        CancellationToken ct = default)
    {
        var callerKey = this.HttpContext.Items["ClientKey"] as string;
        if (string.IsNullOrWhiteSpace(callerKey))
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var callerId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
        if (callerId is null || callerId != clientId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller does not own this client." });
        }

        var deleted = await crawlConfigs.DeleteAsync(configId, clientId, ct);
        if (!deleted)
        {
            return this.NotFound();
        }

        return this.NoContent();
    }

    /// <summary>
    ///     Gets a single client by ID. Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <response code="200">Client found.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClient(Guid clientId)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var client = await clientAdminService.GetByIdAsync(clientId);
        return client is null ? this.NotFound() : this.Ok(ToClientResponse(client));
    }

    /// <summary>
    ///     Lists all registered clients. Requires <c>X-Admin-Key</c>. Client keys are never returned.
    /// </summary>
    /// <response code="200">List of clients.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    [HttpGet("clients")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetClients()
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var clients = await clientAdminService.GetAllAsync();
        return this.Ok(clients.Select(ToClientResponse).ToList());
    }

    /// <summary>
    ///     Lists crawl configurations for the specified client. Requires <c>X-Client-Key</c> that owns the client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of crawl configurations.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    /// <response code="403">Caller does not own this client.</response>
    [HttpGet("clients/{clientId:guid}/crawl-configurations")]
    [ProducesResponseType(typeof(IReadOnlyList<CrawlConfigResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCrawlConfigurations(Guid clientId, CancellationToken ct = default)
    {
        var callerKey = this.HttpContext.Items["ClientKey"] as string;
        if (string.IsNullOrWhiteSpace(callerKey))
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var callerId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
        if (callerId is null || callerId != clientId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller does not own this client." });
        }

        var configs = await crawlConfigs.GetByClientAsync(clientId, ct);
        return this.Ok(
            configs.Select(c => new CrawlConfigResponse(
                    c.Id,
                    c.ClientId,
                    c.OrganizationUrl,
                    c.ProjectId,
                    c.CrawlIntervalSeconds,
                    c.IsActive,
                    c.CreatedAt))
                .ToList());
    }

    /// <summary>
    ///     Updates one or more fields of a client (display name, active status, custom system message).
    ///     Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpPatch("clients/{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchClient(
        Guid clientId,
        [FromBody] PatchClientRequest request,
        [FromServices] IValidator<PatchClientRequest> validator,
        CancellationToken ct = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var client = await clientAdminService.PatchAsync(
            clientId,
            request.IsActive,
            request.DisplayName,
            request.CommentResolutionBehavior,
            request.CustomSystemMessage,
            ct);
        return client is null ? this.NotFound() : this.Ok(ToClientResponse(client));
    }

    /// <summary>
    ///     Enables or disables a crawl configuration. Requires <c>X-Client-Key</c> that owns the client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="configId">Configuration identifier.</param>
    /// <param name="request">Update request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Configuration updated.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">Configuration not found.</response>
    [HttpPatch("clients/{clientId:guid}/crawl-configurations/{configId:guid}")]
    [ProducesResponseType(typeof(CrawlConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchCrawlConfiguration(
        Guid clientId,
        Guid configId,
        [FromBody] PatchCrawlConfigRequest request,
        CancellationToken ct = default)
    {
        var callerKey = this.HttpContext.Items["ClientKey"] as string;
        if (string.IsNullOrWhiteSpace(callerKey))
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var callerId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
        if (callerId is null || callerId != clientId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller does not own this client." });
        }

        var updated = await crawlConfigs.SetActiveAsync(configId, clientId, request.IsActive, ct);
        if (!updated)
        {
            return this.NotFound();
        }

        var configs = await crawlConfigs.GetByClientAsync(clientId, ct);
        var config = configs.First(c => c.Id == configId);

        return this.Ok(
            new CrawlConfigResponse(
                config.Id,
                config.ClientId,
                config.OrganizationUrl,
                config.ProjectId,
                config.CrawlIntervalSeconds,
                config.IsActive,
                config.CreatedAt));
    }

    /// <summary>
    ///     Sets or replaces ADO service principal credentials for a client. Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">ADO credential details — all three fields required.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Credentials stored.</response>
    /// <response code="400">One or more fields are missing or blank.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpPut("clients/{clientId:guid}/ado-credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutAdoCredentials(
        Guid clientId,
        [FromBody] SetAdoCredentialsRequest request,
        [FromServices] IValidator<SetAdoCredentialsRequest> validator,
        CancellationToken ct = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        await adoCredentialRepository.UpsertAsync(
            clientId,
            new ClientAdoCredentials(request.TenantId, request.ClientId, request.Secret),
            ct);

        return this.NoContent();
    }

    /// <summary>
    ///     Sets or replaces the ADO reviewer identity GUID for a client.
    ///     Accepts either <c>X-Admin-Key</c> (any client) or <c>X-Client-Key</c> (own client only).
    ///     Until this is set, review jobs for the client will be rejected.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">The ADO identity GUID of the AI service account.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Reviewer identity stored.</response>
    /// <response code="400"><paramref name="request" /> contains an empty GUID.</response>
    /// <response code="401">Missing or invalid authentication header.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">Client not found.</response>
    [HttpPut("clients/{clientId:guid}/reviewer-identity")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutReviewerIdentity(
        Guid clientId,
        [FromBody] SetReviewerIdentityRequest request,
        [FromServices] IValidator<SetReviewerIdentityRequest> validator,
        CancellationToken ct = default)
    {
        string actorType;

        if (this.HttpContext.Items["IsAdmin"] is true)
        {
            actorType = "Admin";
        }
        else
        {
            var callerKey = this.HttpContext.Items["ClientKey"] as string;
            if (string.IsNullOrWhiteSpace(callerKey))
            {
                return this.Unauthorized(new { error = "Valid X-Admin-Key or X-Client-Key required." });
            }

            var callerId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
            if (callerId is null || callerId != clientId)
            {
                return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller does not own this client." });
            }

            actorType = "Client";
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var found = await clientAdminService.SetReviewerIdentityAsync(clientId, request.ReviewerId, ct);
        if (!found)
        {
            return this.NotFound();
        }

        LogReviewerIdentityUpdated(logger, clientId, actorType);
        return this.NoContent();
    }

    /// <summary>
    ///     Updates the custom AI system message for the authenticated client (self-service).
    ///     Requires <c>X-Client-Key</c>. The client ID is resolved from the key.
    ///     Send an empty string as <c>customSystemMessage</c> to clear an existing value.
    /// </summary>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    [HttpPatch("client/me")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PatchClientMe(
        [FromBody] PatchClientRequest request,
        [FromServices] IValidator<PatchClientRequest> validator,
        CancellationToken ct = default)
    {
        var callerKey = this.HttpContext.Items["ClientKey"] as string;
        if (string.IsNullOrWhiteSpace(callerKey))
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var clientId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
        if (clientId is null)
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var client = await clientAdminService.PatchAsync(
            clientId.Value,
            isActive: null,
            displayName: null,
            commentResolutionBehavior: null,
            request.CustomSystemMessage,
            ct);

        // PatchAsync returns null only when the client doesn't exist;
        // since we resolved the ID from a valid key, this is unexpected.
        return client is null
            ? this.Unauthorized(new { error = "Valid X-Client-Key required." })
            : this.Ok(ToClientResponse(client));
    }

    /// <summary>
    ///     Returns the profile of the specified client. Requires <c>X-Client-Key</c> that owns the client.
    ///     Exposes a subset of client data safe for client-level callers; does not include admin-only fields.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client profile.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}/profile")]
    [ProducesResponseType(typeof(ClientProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClientProfile(Guid clientId, CancellationToken ct = default)
    {
        var callerKey = this.HttpContext.Items["ClientKey"] as string;
        if (string.IsNullOrWhiteSpace(callerKey))
        {
            return this.Unauthorized(new { error = "Valid X-Client-Key required." });
        }

        var callerId = await clientRegistry.GetClientIdByKeyAsync(callerKey, ct);
        if (callerId is null || callerId != clientId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Caller does not own this client." });
        }

        var client = await clientAdminService.GetByIdAsync(clientId, ct);
        if (client is null)
        {
            return this.NotFound();
        }

        return this.Ok(
            new ClientProfileResponse(
                client.Id,
                client.DisplayName,
                client.IsActive,
                client.CreatedAt,
                client.ReviewerId));
    }
}

/// <summary>Client response — key, ADO secret, and credential metadata are never included.</summary>
public sealed record ClientResponse(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    bool HasAdoCredentials,
    Guid? ReviewerId,
    CommentResolutionBehavior CommentResolutionBehavior,
    string? CustomSystemMessage);

/// <summary>Crawl configuration response.</summary>
public sealed record CrawlConfigResponse(
    Guid Id,
    Guid ClientId,
    string OrganizationUrl,
    string ProjectId,
    int CrawlIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>Request body for creating a client.</summary>
public sealed record CreateClientRequest(string Key, string DisplayName);

/// <summary>Request body for creating a crawl configuration.</summary>
public sealed record CreateCrawlConfigRequest(
    string OrganizationUrl,
    string ProjectId,
    int CrawlIntervalSeconds = 60);

/// <summary>
///     Request body for patching a client. All fields are optional; omitted fields are left unchanged.
///     Set <see cref="CustomSystemMessage" /> to <c>""</c> (empty string) to clear an existing value.
/// </summary>
public sealed record PatchClientRequest(
    bool? IsActive = null,
    string? DisplayName = null,
    CommentResolutionBehavior? CommentResolutionBehavior = null,
    string? CustomSystemMessage = null);

/// <summary>Request body for patching a crawl configuration's active status.</summary>
public sealed record PatchCrawlConfigRequest(bool IsActive);

/// <summary>Request body for setting ADO service principal credentials.</summary>
public sealed record SetAdoCredentialsRequest(string TenantId, string ClientId, string Secret);

/// <summary>Request body for setting the ADO reviewer identity on a client.</summary>
public sealed record SetReviewerIdentityRequest(Guid ReviewerId);

/// <summary>
///     Client profile response — exposes only fields safe for client-level callers.
///     Admin-only fields such as <c>HasAdoCredentials</c> are intentionally omitted.
/// </summary>
public sealed record ClientProfileResponse(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    Guid? ReviewerId);
