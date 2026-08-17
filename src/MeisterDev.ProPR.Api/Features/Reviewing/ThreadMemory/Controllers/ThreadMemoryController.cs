// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Admin endpoints for managing thread memory embeddings and the memory activity log.</summary>
[ApiController]
[Route("admin/reviewing/thread-memory")]
public sealed partial class ThreadMemoryController(
    IThreadMemoryRepository memoryRepository,
    IReviewPrScanThreadStatusStore scanRepository,
    IMemoryActivityLog activityLog,
    ILogger<ThreadMemoryController> logger) : ControllerBase
{
    private IActionResult? RequireAdmin()
    {
        return AuthHelpers.RequireAdmin(this.HttpContext);
    }

    /// <summary>
    ///     Returns a paginated list of stored thread memory embeddings for the given client.
    ///     Optionally filters by a search term matched against file path, repository ID, or resolution summary.
    /// </summary>
    /// <param name="clientId">Owning client ID. Required.</param>
    /// <param name="search">Optional free-text search.</param>
    /// <param name="page">Page number (1-based, default 1).</param>
    /// <param name="pageSize">Page size (default 50, max 200).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Paginated list of stored embeddings.</response>
    /// <response code="400">Missing or invalid parameters.</response>
    /// <response code="403">Caller is not an admin.</response>
    [HttpGet]
    [HttpGet("/admin/thread-memory")]
    [ProducesResponseType(typeof(PagedResult<ThreadMemoryRecordDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStoredEmbeddings(
        [FromQuery] Guid clientId,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        if (clientId == Guid.Empty)
        {
            return this.BadRequest(new { error = "clientId is required." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var result = await memoryRepository.GetPagedAsync(clientId, search, page, pageSize, ct: ct);
        var dtoItems = result.Items.Select(ToDto).ToList().AsReadOnly();
        return this.Ok(new PagedResult<ThreadMemoryRecordDto>(dtoItems, result.TotalCount, result.Page, result.PageSize));
    }

    /// <summary>
    ///     Deletes the stored embedding with the given ID (scoped to the owning client).
    ///     Also resets <c>last_seen_status</c> to <see langword="null" /> on the corresponding scan thread
    ///     so the next crawl cycle will re-evaluate the thread.
    ///     Idempotent — returns 204 even if the record does not exist.
    /// </summary>
    /// <param name="id">Embedding record ID.</param>
    /// <param name="clientId">Owning client ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Record deleted (or did not exist).</response>
    /// <response code="403">Caller is not an admin.</response>
    [HttpDelete("{id:guid}")]
    [HttpDelete("/admin/thread-memory/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteEmbedding(
        Guid id,
        [FromQuery] Guid clientId,
        CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        if (clientId == Guid.Empty)
        {
            return this.BadRequest(new { error = "clientId is required." });
        }

        var records = await memoryRepository.GetPagedAsync(clientId, null, 1, 1, ct: ct);
        var existing = records.Items.FirstOrDefault(r => r.Id == id);

        var deleted = await memoryRepository.RemoveByIdAsync(id, clientId, ct);

        if (deleted && existing is not null)
        {
            await this.ResetLastSeenStatusAsync(
                clientId,
                existing.OrganizationUrl,
                existing.ProjectId,
                existing.RepositoryId,
                existing.PullRequestId,
                existing.ThreadId,
                ct);

            await activityLog.AppendAsync(
                new MemoryActivityLogEntry
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    ThreadId = existing.ThreadId,
                    RepositoryId = existing.RepositoryId,
                    PullRequestId = existing.PullRequestId,
                    Action = MemoryActivityAction.Removed,
                    PreviousStatus = "resolved",
                    CurrentStatus = "reset",
                    Reason = "admin_deleted",
                    OccurredAt = DateTimeOffset.UtcNow,
                },
                ct);

            LogEmbeddingDeleted(logger, id, clientId);
        }

        return this.NoContent();
    }

    /// <summary>
    ///     Returns a paginated list of memory activity log entries for the given client.
    /// </summary>
    /// <param name="clientId">Owning client ID. Required.</param>
    /// <param name="threadId">Optional: filter by thread ID.</param>
    /// <param name="pullRequestId">Optional: filter by pull request ID.</param>
    /// <param name="repositoryId">Optional: filter by repository ID.</param>
    /// <param name="action">Optional: filter by action (0=Stored, 1=Removed, 2=NoOp).</param>
    /// <param name="from">Optional: earliest occurrence timestamp (inclusive).</param>
    /// <param name="to">Optional: latest occurrence timestamp (inclusive).</param>
    /// <param name="page">Page number (1-based, default 1).</param>
    /// <param name="pageSize">Page size (default 50, max 200).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Paginated list of activity log entries.</response>
    /// <response code="403">Caller is not an admin.</response>
    [HttpGet("activity-log")]
    [HttpGet("/admin/thread-memory/activity-log")]
    [ProducesResponseType(typeof(PagedResult<MemoryActivityLogEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActivityLog(
        [FromQuery] Guid clientId,
        [FromQuery] string? threadId = null,
        [FromQuery] int? pullRequestId = null,
        [FromQuery] string? repositoryId = null,
        [FromQuery] MemoryActivityAction? action = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        if (clientId == Guid.Empty)
        {
            return this.BadRequest(new { error = "clientId is required." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = new MemoryActivityLogQuery(
            threadId,
            pullRequestId,
            repositoryId,
            action,
            from,
            to,
            page,
            pageSize);

        var result = await activityLog.QueryAsync(clientId, query, ct);
        return this.Ok(result);
    }

    private async Task ResetLastSeenStatusAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string threadId,
        CancellationToken ct)
    {
        try
        {
            var scan = await scanRepository.GetAsync(
                clientId,
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                ct);
            if (scan?.Threads.Any(thread => string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))
                is not true)
            {
                return;
            }

            await scanRepository.SetLastSeenStatusesAsync(
                clientId,
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                new Dictionary<string, string?>(StringComparer.Ordinal) { [threadId] = null },
                ct);
        }
        catch (Exception ex)
        {
            LogResetLastSeenStatusFailed(logger, threadId, ex);
        }
    }

    private static ThreadMemoryRecordDto ToDto(ThreadMemoryRecord r)
    {
        return new ThreadMemoryRecordDto(
            r.Id,
            r.ClientId,
            r.ThreadId,
            r.RepositoryId,
            r.PullRequestId,
            r.FilePath,
            r.ResolutionSummary,
            r.CreatedAt,
            r.UpdatedAt,
            r.Keywords,
            r.CodeInsightFindingId,
            r.MemorySource,
            r.ResolutionIntent,
            r.ResolutionClarity);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding {Id} deleted by admin for client {ClientId}")]
    private static partial void LogEmbeddingDeleted(ILogger logger, Guid id, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to reset last_seen_status for thread {ThreadId} after admin deletion")]
    private static partial void LogResetLastSeenStatusFailed(ILogger logger, string threadId, Exception ex);
}

/// <summary>DTO for a stored thread memory embedding (admin view).</summary>
/// <param name="Id">Record identifier.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="ThreadId">Provider-native thread identifier.</param>
/// <param name="RepositoryId">ADO repository ID.</param>
/// <param name="PullRequestId">ADO pull request number.</param>
/// <param name="FilePath">File path, if any.</param>
/// <param name="ResolutionSummary">AI-generated summary.</param>
/// <param name="CreatedAt">When the record was first stored.</param>
/// <param name="UpdatedAt">When the record was last upserted.</param>
/// <param name="Keywords">Searchable keywords derived for code insights.</param>
/// <param name="CodeInsightFindingId">The finding this memory came from, when known.</param>
/// <param name="Source">Whether the record came from a resolved thread or an administrator dismissal.</param>
/// <param name="ResolutionIntent">
///     What the reviewer's resolution meant: a rejection of the finding, or a claim that it was fixed.
///     <see langword="null" /> for a record written before the outcome was kept, and for an administrator
///     dismissal, which carries no reviewer decision.
/// </param>
/// <param name="ResolutionClarity">
///     How plainly the discussion stated the resolution. Distinguishes a rejection a reviewer made explicit
///     from one inferred from an unclear thread.
/// </param>
public sealed record ThreadMemoryRecordDto(
    Guid Id,
    Guid ClientId,
    string ThreadId,
    string RepositoryId,
    int PullRequestId,
    string? FilePath,
    string ResolutionSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Keywords = null,
    Guid? CodeInsightFindingId = null,
    MemorySource Source = MemorySource.ThreadResolved,
    ThreadResolutionIntent? ResolutionIntent = null,
    ResolutionClarity? ResolutionClarity = null);
