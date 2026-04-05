// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Admin endpoints for managing thread memory embeddings and the memory activity log.</summary>
[ApiController]
public sealed partial class ThreadMemoryController(
    IThreadMemoryRepository memoryRepository,
    IReviewPrScanRepository scanRepository,
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

        // Resolve the record first (for activity-log metadata) — not strictly required but provides richer tracing.
        var records = await memoryRepository.GetPagedAsync(clientId, null, 1, 1, ct: ct);
        var existing = records.Items.FirstOrDefault(r => r.Id == id);

        var deleted = await memoryRepository.RemoveByIdAsync(id, clientId, ct);

        if (deleted && existing is not null)
        {
            // Reset LastSeenStatus on the scan thread so the next crawl will re-embed.
            await this.ResetLastSeenStatusAsync(clientId, existing.RepositoryId, existing.PullRequestId, existing.ThreadId, ct);

            // Append activity log entry for admin deletion.
            await activityLog.AppendAsync(new MemoryActivityLogEntry
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
            }, ct);

            LogEmbeddingDeleted(logger, id, clientId);
        }

        return this.NoContent();
    }

    /// <summary>
    ///     Dismisses a finding by storing it as an admin-dismissed memory record.
    ///     Future reviews will suppress similar findings via the memory reconsideration pipeline.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Dismiss request with the finding message and optional label.</param>
    /// <param name="memoryService">Service used to persist the dismissal as thread memory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Finding dismissed and memory record created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="403">Caller is not an admin.</response>
    [HttpPost("/clients/{clientId:guid}/dismiss-finding")]
    [ProducesResponseType(typeof(ThreadMemoryRecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DismissFinding(
        Guid clientId,
        [FromBody] DismissFindingRequest request,
        [FromServices] IThreadMemoryService memoryService,
        CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(request.FindingMessage))
        {
            return this.BadRequest(new { error = "findingMessage is required." });
        }

        var record = await memoryService.DismissFindingAsync(
            clientId,
            request.FilePath,
            request.FindingMessage,
            request.Label,
            ct);

        LogDismissalCreated(logger, record.Id, clientId);

        return this.StatusCode(201, ToDto(record));
    }

    /// <summary>
    ///     Returns a paginated list of memory activity log entries for the given client.</summary>
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
    [HttpGet("/admin/thread-memory/activity-log")]
    [ProducesResponseType(typeof(PagedResult<MemoryActivityLogEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActivityLog(
        [FromQuery] Guid clientId,
        [FromQuery] int? threadId = null,
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
            ThreadId: threadId,
            PullRequestId: pullRequestId,
            RepositoryId: repositoryId,
            Action: action,
            FromDate: from,
            ToDate: to,
            Page: page,
            PageSize: pageSize);

        var result = await activityLog.QueryAsync(clientId, query, ct);
        return this.Ok(result);
    }

    private async Task ResetLastSeenStatusAsync(Guid clientId, string repositoryId, int pullRequestId, int threadId, CancellationToken ct)
    {
        try
        {
            var scan = await scanRepository.GetAsync(clientId, repositoryId, pullRequestId, ct);
            if (scan is null)
            {
                return;
            }

            var thread = scan.Threads.FirstOrDefault(t => t.ThreadId == threadId);
            if (thread is null)
            {
                return;
            }

            // Rebuild the scan record with the thread's LastSeenStatus cleared.
            var updatedScan = new Domain.Entities.ReviewPrScan(
                scan.Id, scan.ClientId, scan.RepositoryId, scan.PullRequestId, scan.LastProcessedCommitId);

            foreach (var t in scan.Threads)
            {
                updatedScan.Threads.Add(new Domain.Entities.ReviewPrScanThread
                {
                    ReviewPrScanId = scan.Id,
                    ThreadId = t.ThreadId,
                    LastSeenReplyCount = t.LastSeenReplyCount,
                    LastSeenStatus = t.ThreadId == threadId ? null : t.LastSeenStatus,
                });
            }

            await scanRepository.UpsertAsync(updatedScan, ct);
        }
        catch (Exception ex)
        {
            LogResetLastSeenStatusFailed(logger, threadId, ex);
        }
    }

    private static ThreadMemoryRecordDto ToDto(ThreadMemoryRecord r) =>
        new(r.Id, r.ClientId, r.ThreadId, r.RepositoryId, r.PullRequestId,
            r.FilePath, r.ResolutionSummary, r.CreatedAt, r.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedding {Id} deleted by admin for client {ClientId}")]
    private static partial void LogEmbeddingDeleted(ILogger logger, Guid id, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finding dismissal memory record {Id} created for client {ClientId}")]
    private static partial void LogDismissalCreated(ILogger logger, Guid id, Guid clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to reset last_seen_status for thread {ThreadId} after admin deletion")]
    private static partial void LogResetLastSeenStatusFailed(ILogger logger, int threadId, Exception ex);
}

/// <summary>DTO for a stored thread memory embedding (admin view).</summary>
/// <param name="Id">Record identifier.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="ThreadId">ADO thread ID.</param>
/// <param name="RepositoryId">ADO repository ID.</param>
/// <param name="PullRequestId">ADO pull request number.</param>
/// <param name="FilePath">File path, if any.</param>
/// <param name="ResolutionSummary">AI-generated summary.</param>
/// <param name="CreatedAt">When the record was first stored.</param>
/// <param name="UpdatedAt">When the record was last upserted.</param>
public sealed record ThreadMemoryRecordDto(
    Guid Id,
    Guid ClientId,
    int ThreadId,
    string RepositoryId,
    int PullRequestId,
    string? FilePath,
    string ResolutionSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Request to dismiss a finding and store it as an admin-dismissed memory record.</summary>
/// <param name="FindingMessage">The original finding message to dismiss.</param>
/// <param name="FilePath">Optional file path to scope the dismissal.</param>
/// <param name="Label">Optional human-readable label for the dismissal.</param>
public sealed record DismissFindingRequest(string FindingMessage, string? FilePath = null, string? Label = null);
