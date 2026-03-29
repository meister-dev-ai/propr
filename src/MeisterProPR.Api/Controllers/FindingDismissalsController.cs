using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages per-client AI reviewer finding dismissals.</summary>
[ApiController]
public sealed partial class FindingDismissalsController(
    IFindingDismissalRepository dismissalRepository,
    ILogger<FindingDismissalsController> logger) : ControllerBase
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Finding dismissal {DismissalId} created for client {ClientId}")]
    private static partial void LogDismissalCreated(ILogger logger, Guid dismissalId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finding dismissal {DismissalId} updated for client {ClientId}")]
    private static partial void LogDismissalUpdated(ILogger logger, Guid dismissalId, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finding dismissal {DismissalId} deleted for client {ClientId}")]
    private static partial void LogDismissalDeleted(ILogger logger, Guid dismissalId, Guid clientId);

    /// <summary>Lists all finding dismissals for the specified client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">List of dismissals.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    [HttpGet("clients/{clientId:guid}/finding-dismissals")]
    [ProducesResponseType(typeof(IReadOnlyList<FindingDismissalDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListDismissals(Guid clientId, CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        var dismissals = await dismissalRepository.GetByClientAsync(clientId, ct);
        return this.Ok(dismissals.Select(d => new FindingDismissalDto(d.Id, d.ClientId, d.PatternText, d.Label, d.OriginalMessage, d.CreatedAt)));
    }

    /// <summary>Creates a new finding dismissal for the specified client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Dismissal details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Dismissal created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    [HttpPost("clients/{clientId:guid}/finding-dismissals")]
    [ProducesResponseType(typeof(FindingDismissalDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateDismissal(
        Guid clientId,
        [FromBody] CreateFindingDismissalRequest request,
        CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        if (string.IsNullOrWhiteSpace(request.OriginalMessage))
        {
            this.ModelState.AddModelError(nameof(request.OriginalMessage), "originalMessage is required.");
            return this.ValidationProblem();
        }

        var patternText = NormalizePattern(request.OriginalMessage);

        var dismissal = new FindingDismissal(
            Guid.NewGuid(),
            clientId,
            patternText,
            request.Label,
            request.OriginalMessage);

        await dismissalRepository.AddAsync(dismissal, ct);

        LogDismissalCreated(logger, dismissal.Id, clientId);

        var dto = new FindingDismissalDto(dismissal.Id, dismissal.ClientId, dismissal.PatternText, dismissal.Label, dismissal.OriginalMessage, dismissal.CreatedAt);
        return this.CreatedAtAction(nameof(this.ListDismissals), new { clientId }, dto);
    }

    /// <summary>Updates the label of an existing finding dismissal.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="id">Dismissal identifier.</param>
    /// <param name="request">Label update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Dismissal updated.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    /// <response code="404">Dismissal not found.</response>
    [HttpPatch("clients/{clientId:guid}/finding-dismissals/{id:guid}")]
    [ProducesResponseType(typeof(FindingDismissalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDismissal(
        Guid clientId,
        Guid id,
        [FromBody] UpdateFindingDismissalRequest request,
        CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        var dismissal = await dismissalRepository.GetByIdAsync(id, ct);
        if (dismissal is null || dismissal.ClientId != clientId)
        {
            return this.NotFound();
        }

        dismissal.UpdateLabel(request.Label);
        await dismissalRepository.UpdateAsync(dismissal, ct);

        LogDismissalUpdated(logger, id, clientId);

        var dto = new FindingDismissalDto(dismissal.Id, dismissal.ClientId, dismissal.PatternText, dismissal.Label, dismissal.OriginalMessage, dismissal.CreatedAt);
        return this.Ok(dto);
    }

    /// <summary>Deletes a finding dismissal.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="id">Dismissal identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Dismissal deleted.</response>
    /// <response code="401">Missing or invalid admin key.</response>
    /// <response code="404">Dismissal not found.</response>
    [HttpDelete("clients/{clientId:guid}/finding-dismissals/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDismissal(
        Guid clientId,
        Guid id,
        CancellationToken ct = default)
    {
        if (!this.IsAdmin())
        {
            return this.Unauthorized(new { error = "X-Admin-Key required." });
        }

        var dismissal = await dismissalRepository.GetByIdAsync(id, ct);
        if (dismissal is null || dismissal.ClientId != clientId)
        {
            return this.NotFound();
        }

        await dismissalRepository.DeleteAsync(id, ct);

        LogDismissalDeleted(logger, id, clientId);

        return this.NoContent();
    }

    private bool IsAdmin() => this.HttpContext.Items["IsAdmin"] is true;

    /// <summary>
    ///     Normalizes a finding message into a comparable pattern text.
    ///     Lowercase, max 200 characters, punctuation stripped.
    /// </summary>
    private static string NormalizePattern(string message)
    {
        var normalized = new System.Text.StringBuilder();
        foreach (var ch in message.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == ' ')
            {
                normalized.Append(ch);
            }
        }

        var result = normalized.ToString().Trim();
        return result.Length <= 200 ? result : result[..200];
    }
}

/// <summary>DTO for a finding dismissal.</summary>
public sealed record FindingDismissalDto(
    Guid Id,
    Guid ClientId,
    string PatternText,
    string? Label,
    string OriginalMessage,
    DateTimeOffset CreatedAt);

/// <summary>Request to create a finding dismissal.</summary>
public sealed record CreateFindingDismissalRequest(string OriginalMessage, string? Label = null);

/// <summary>Request to update a finding dismissal's label.</summary>
public sealed record UpdateFindingDismissalRequest(string? Label);
