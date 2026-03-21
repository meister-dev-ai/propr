using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages clients (admin) and crawl configurations (client-scoped).</summary>
[ApiController]
public sealed class ClientsController(
    MeisterProPRDbContext dbContext,
    IClientRegistry clientRegistry,
    ICrawlConfigurationRepository crawlConfigs,
    IClientAdoCredentialRepository adoCredentialRepository) : ControllerBase
{
    private static ClientResponse ToClientResponse(ClientRecord client)
    {
        return new ClientResponse(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client is { AdoTenantId: not null, AdoClientId: not null, AdoClientSecret: not null },
            client.AdoTenantId,
            client.AdoClientId,
            client.ReviewerId);
    }

    /// <summary>
    ///     Adds a crawl configuration for the specified client. Requires <c>X-Client-Key</c> that owns the client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Crawl configuration details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Configuration created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid <c>X-Client-Key</c>.</response>
    /// <response code="403">Caller does not own this client.</response>
    /// <response code="404">Client not found.</response>
    [HttpPost("clients/{clientId:guid}/crawl-configurations")]
    [ProducesResponseType(typeof(CrawlConfigResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddCrawlConfiguration(
        Guid clientId,
        [FromBody] CreateCrawlConfigRequest request,
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

        var clientExists = await dbContext.Clients.AnyAsync(c => c.Id == clientId, ct);
        if (!clientExists)
        {
            return this.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.OrganizationUrl))
        {
            return this.BadRequest(new { error = "OrganizationUrl is required." });
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return this.BadRequest(new { error = "ProjectId is required." });
        }

        if (request.CrawlIntervalSeconds < 10)
        {
            return this.BadRequest(new { error = "CrawlIntervalSeconds must be >= 10." });
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
    /// <response code="201">Client registered.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="409">A client with that key already exists.</response>
    [HttpPost("clients")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateClient([FromBody] CreateClientRequest request)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        if (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length < 16)
        {
            return this.BadRequest(new { error = "Key must be at least 16 characters." });
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return this.BadRequest(new { error = "DisplayName is required." });
        }

        var exists = await dbContext.Clients.AnyAsync(c => c.Key == request.Key);
        if (exists)
        {
            return this.Conflict(new { error = "A client with that key already exists." });
        }

        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            Key = request.Key,
            DisplayName = request.DisplayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Clients.Add(client);
        await dbContext.SaveChangesAsync();

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

        var clientExists = await dbContext.Clients.AnyAsync(c => c.Id == clientId, ct);
        if (!clientExists)
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

        var client = await dbContext.Clients.FindAsync(clientId);
        if (client is null)
        {
            return this.NotFound();
        }

        dbContext.Clients.Remove(client);
        await dbContext.SaveChangesAsync();
        return this.NoContent();
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

        var client = await dbContext.Clients.FindAsync(clientId);
        if (client is null)
        {
            return this.NotFound();
        }

        return this.Ok(ToClientResponse(client));
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

        var clients = await dbContext.Clients
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

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
    ///     Updates one or more fields of a client (display name, active status). Requires <c>X-Admin-Key</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <response code="200">Client updated.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpPatch("clients/{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] PatchClientRequest request)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        var client = await dbContext.Clients.FindAsync(clientId);
        if (client is null)
        {
            return this.NotFound();
        }

        if (request.IsActive.HasValue)
        {
            client.IsActive = request.IsActive.Value;
        }

        if (request.DisplayName is not null)
        {
            client.DisplayName = request.DisplayName;
        }

        await dbContext.SaveChangesAsync();

        return this.Ok(ToClientResponse(client));
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
        CancellationToken ct = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        if (string.IsNullOrWhiteSpace(request.TenantId) ||
            string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.Secret))
        {
            return this.BadRequest(new { error = "TenantId, ClientId, and Secret are all required." });
        }

        var clientExists = await dbContext.Clients.AnyAsync(c => c.Id == clientId, ct);
        if (!clientExists)
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
    ///     Sets or replaces the ADO reviewer identity GUID for a client. Requires <c>X-Admin-Key</c>.
    ///     Until this is set, review jobs for the client will be rejected.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">The ADO identity GUID of the AI service account.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Reviewer identity stored.</response>
    /// <response code="400"><paramref name="request" /> contains an empty GUID.</response>
    /// <response code="401">Missing or invalid <c>X-Admin-Key</c>.</response>
    /// <response code="404">Client not found.</response>
    [HttpPut("clients/{clientId:guid}/reviewer-identity")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutReviewerIdentity(
        Guid clientId,
        [FromBody] SetReviewerIdentityRequest request,
        CancellationToken ct = default)
    {
        if (this.HttpContext.Items["IsAdmin"] is not true)
        {
            return this.Unauthorized(new { error = "Valid X-Admin-Key required." });
        }

        if (request.ReviewerId == Guid.Empty)
        {
            return this.BadRequest(new { error = "ReviewerId must not be an empty GUID." });
        }

        var client = await dbContext.Clients.FindAsync([clientId], ct);
        if (client is null)
        {
            return this.NotFound();
        }

        client.ReviewerId = request.ReviewerId;
        await dbContext.SaveChangesAsync(ct);

        return this.NoContent();
    }

    /// <summary>Client response — key and ADO secret are never included.</summary>
    public sealed record ClientResponse(
        Guid Id,
        string DisplayName,
        bool IsActive,
        DateTimeOffset CreatedAt,
        bool HasAdoCredentials,
        string? AdoTenantId,
        string? AdoClientId,
        Guid? ReviewerId);

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

    /// <summary>Request body for patching a client. All fields are optional; omitted fields are left unchanged.</summary>
    public sealed record PatchClientRequest(bool? IsActive = null, string? DisplayName = null);

    /// <summary>Request body for patching a crawl configuration's active status.</summary>
    public sealed record PatchCrawlConfigRequest(bool IsActive);

    /// <summary>Request body for setting ADO service principal credentials.</summary>
    public sealed record SetAdoCredentialsRequest(string TenantId, string ClientId, string Secret);

    /// <summary>Request body for setting the ADO reviewer identity on a client.</summary>
    public sealed record SetReviewerIdentityRequest(Guid ReviewerId);
}
