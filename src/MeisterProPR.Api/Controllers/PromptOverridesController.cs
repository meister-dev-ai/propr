using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages per-client and per-crawl-config AI review prompt overrides.</summary>
[ApiController]
public sealed partial class PromptOverridesController(
    IPromptOverrideService promptOverrideService,
    ILogger<PromptOverridesController> logger) : ControllerBase
{
    private static readonly IReadOnlySet<string> ValidPromptKeys = PromptOverride.ValidPromptKeys;

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt override {OverrideId} created for client {ClientId}")]
    private static partial void LogOverrideCreated(ILogger logger, Guid overrideId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt override {OverrideId} updated for client {ClientId}")]
    private static partial void LogOverrideUpdated(ILogger logger, Guid overrideId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt override {OverrideId} deleted for client {ClientId}")]
    private static partial void LogOverrideDeleted(ILogger logger, Guid overrideId, Guid clientId);

    /// <summary>Lists all prompt overrides for the specified client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of prompt overrides.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    [HttpGet("clients/{clientId:guid}/prompt-overrides")]
    [ProducesResponseType(typeof(IReadOnlyList<PromptOverrideDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListOverrides(Guid clientId, CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        var overrides = await promptOverrideService.ListByClientAsync(clientId, ct);
        return this.Ok(overrides);
    }

    /// <summary>Creates a new prompt override for the specified client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Override details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Prompt override created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    /// <response code="409">An override with the same scope and prompt key already exists for this client / crawl config.</response>
    [HttpPost("clients/{clientId:guid}/prompt-overrides")]
    [ProducesResponseType(typeof(PromptOverrideDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOverride(
        Guid clientId,
        [FromBody] CreatePromptOverrideRequest request,
        CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        if (string.IsNullOrWhiteSpace(request.PromptKey) || !ValidPromptKeys.Contains(request.PromptKey))
        {
            this.ModelState.AddModelError(nameof(request.PromptKey), $"promptKey must be one of: {string.Join(", ", ValidPromptKeys)}.");
            return this.ValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(request.OverrideText))
        {
            this.ModelState.AddModelError(nameof(request.OverrideText), "overrideText is required.");
            return this.ValidationProblem();
        }

        if (request.Scope != "clientScope" && request.Scope != "crawlConfigScope")
        {
            this.ModelState.AddModelError(nameof(request.Scope), "scope must be 'clientScope' or 'crawlConfigScope'.");
            return this.ValidationProblem();
        }

        var scope = request.Scope == "crawlConfigScope"
            ? PromptOverrideScope.CrawlConfigScope
            : PromptOverrideScope.ClientScope;

        if (scope == PromptOverrideScope.CrawlConfigScope && request.CrawlConfigId is null)
        {
            this.ModelState.AddModelError(nameof(request.CrawlConfigId), "crawlConfigId is required when scope is crawlConfigScope.");
            return this.ValidationProblem();
        }

        try
        {
            var dto = await promptOverrideService.CreateAsync(
                clientId,
                scope,
                request.CrawlConfigId,
                request.PromptKey,
                request.OverrideText,
                ct);

            LogOverrideCreated(logger, dto.Id, clientId);
            return this.CreatedAtAction(nameof(this.ListOverrides), new { clientId }, dto);
        }
        catch (DuplicatePromptOverrideException)
        {
            return this.Conflict(new { error = "A prompt override with this scope and key already exists." });
        }
    }

    /// <summary>Replaces the override text of an existing prompt override.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="id">Override identifier.</param>
    /// <param name="request">Update details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Prompt override updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    /// <response code="404">Override not found.</response>
    [HttpPut("clients/{clientId:guid}/prompt-overrides/{id:guid}")]
    [ProducesResponseType(typeof(PromptOverrideDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOverride(
        Guid clientId,
        Guid id,
        [FromBody] UpdatePromptOverrideRequest request,
        CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        if (string.IsNullOrWhiteSpace(request.OverrideText))
        {
            this.ModelState.AddModelError(nameof(request.OverrideText), "overrideText is required.");
            return this.ValidationProblem();
        }

        var dto = await promptOverrideService.UpdateAsync(clientId, id, request.OverrideText, ct);
        if (dto is null)
        {
            return this.NotFound();
        }

        LogOverrideUpdated(logger, id, clientId);
        return this.Ok(dto);
    }

    /// <summary>Deletes a prompt override.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="id">Override identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Prompt override deleted.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    /// <response code="404">Override not found.</response>
    [HttpDelete("clients/{clientId:guid}/prompt-overrides/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteOverride(
        Guid clientId,
        Guid id,
        CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        var deleted = await promptOverrideService.DeleteAsync(clientId, id, ct);
        if (!deleted)
        {
            return this.NotFound();
        }

        LogOverrideDeleted(logger, id, clientId);
        return this.NoContent();
    }

    private bool IsAdmin() => this.HttpContext.Items["IsAdmin"] is true;
}

/// <summary>Request to create a prompt override.</summary>
/// <param name="Scope">Override scope: <c>clientScope</c> or <c>crawlConfigScope</c>.</param>
/// <param name="CrawlConfigId">Required when scope is <c>crawlConfigScope</c>.</param>
/// <param name="PromptKey">Named prompt segment to override.</param>
/// <param name="OverrideText">Full replacement text.</param>
public sealed record CreatePromptOverrideRequest(
    string Scope,
    string PromptKey,
    string OverrideText,
    Guid? CrawlConfigId = null);

/// <summary>Request to replace the override text of an existing prompt override.</summary>
/// <param name="OverrideText">New full replacement text.</param>
public sealed record UpdatePromptOverrideRequest(string OverrideText);
