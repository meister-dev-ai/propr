// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Read endpoints that serve the opt-in-retained raw pull-request data (archived discussion threads
///     and per-file diffs) for the in-app pull-request view. Authorized like the other review-read
///     endpoints: the caller needs at least read (<see cref="ClientRole.ClientUser" />) access to the
///     owning client, and a global admin passes for any client. All data is read from the review-archive
///     store; an empty result simply means nothing is retained for the pull request, so the caller can
///     degrade gracefully.
/// </summary>
[ApiController]
public sealed class RetainedPullRequestDataController(IReviewArchiveStore reviewArchiveStore) : ControllerBase
{
    /// <summary>
    ///     Returns the retained discussion threads (with their comments) for a pull request. Each comment
    ///     carries its author identity, whether it was AI-authored, its status, publication timestamp, and
    ///     body. An empty array means no thread data is retained for the pull request.
    /// </summary>
    /// <param name="clientId">Owning client identifier (route).</param>
    /// <param name="repositoryId">Provider repository identifier (may contain slashes or other path-like characters).</param>
    /// <param name="pullRequestId">Provider pull-request identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Retained threads for the pull request (possibly empty).</response>
    /// <response code="400">Missing or invalid parameters.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks read access to the client.</response>
    [HttpGet("/clients/{clientId:guid}/review-archive/pull-requests/threads")]
    [ProducesResponseType(typeof(IReadOnlyList<RetainedThreadDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRetainedThreads(
        Guid clientId,
        [FromQuery] string repositoryId,
        [FromQuery] long pullRequestId,
        CancellationToken ct = default)
    {
        var validation = this.ValidateLookup(clientId, repositoryId);
        if (validation is not null)
        {
            return validation;
        }

        var threads = await reviewArchiveStore.GetThreadsForPullRequestAsync(clientId, repositoryId, pullRequestId, ct);
        var dtos = threads
            .Select(thread => new RetainedThreadDto(
                thread.ThreadId,
                thread.FilePath,
                thread.Line,
                thread.Status,
                thread.UpdatedAt,
                thread.Comments
                    .Select(comment => new RetainedCommentDto(
                        comment.CommentId,
                        comment.AuthorIdentity,
                        comment.IsAiAuthored,
                        comment.PublishedAt,
                        comment.Text,
                        comment.OriginatingJobId))
                    .ToList()
                    .AsReadOnly()))
            .ToList()
            .AsReadOnly();

        return this.Ok(dtos);
    }

    /// <summary>
    ///     Returns the list of retained files for a pull request, each collapsed to its newest retained
    ///     revision. The diff text is not included. An empty array means no diff data is retained for the
    ///     pull request.
    /// </summary>
    /// <param name="clientId">Owning client identifier (route).</param>
    /// <param name="repositoryId">Provider repository identifier (may contain slashes or other path-like characters).</param>
    /// <param name="pullRequestId">Provider pull-request identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Retained files for the pull request (possibly empty).</response>
    /// <response code="400">Missing or invalid parameters.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks read access to the client.</response>
    [HttpGet("/clients/{clientId:guid}/review-archive/pull-requests/files")]
    [ProducesResponseType(typeof(IReadOnlyList<RetainedFileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRetainedFiles(
        Guid clientId,
        [FromQuery] string repositoryId,
        [FromQuery] long pullRequestId,
        CancellationToken ct = default)
    {
        var validation = this.ValidateLookup(clientId, repositoryId);
        if (validation is not null)
        {
            return validation;
        }

        var files = await reviewArchiveStore.ListRetainedFilesForPullRequestAsync(clientId, repositoryId, pullRequestId, ct);
        var dtos = files
            .Select(file => new RetainedFileDto(
                file.FilePath,
                file.RevisionKey,
                file.ChangeType,
                file.IsBinary,
                file.CreatedAt))
            .ToList()
            .AsReadOnly();

        return this.Ok(dtos);
    }

    /// <summary>
    ///     Returns a single retained file's stored unified diff for a pull request. When no revision is
    ///     supplied the newest retained revision for the file is returned.
    /// </summary>
    /// <param name="clientId">Owning client identifier (route).</param>
    /// <param name="repositoryId">Provider repository identifier (may contain slashes or other path-like characters).</param>
    /// <param name="pullRequestId">Provider pull-request identifier.</param>
    /// <param name="filePath">Repository-relative file path whose stored diff should be returned.</param>
    /// <param name="revisionKey">Optional review increment to return; defaults to the newest retained revision.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The retained file diff.</response>
    /// <response code="400">Missing or invalid parameters.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks read access to the client.</response>
    /// <response code="404">No diff is retained for the file.</response>
    [HttpGet("/clients/{clientId:guid}/review-archive/pull-requests/file-diff")]
    [ProducesResponseType(typeof(RetainedFileDiffDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRetainedFileDiff(
        Guid clientId,
        [FromQuery] string repositoryId,
        [FromQuery] long pullRequestId,
        [FromQuery] string filePath,
        [FromQuery] string? revisionKey = null,
        CancellationToken ct = default)
    {
        var validation = this.ValidateLookup(clientId, repositoryId);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return this.BadRequest(new { error = "filePath is required." });
        }

        var diff = await reviewArchiveStore.GetFileDiffAsync(clientId, repositoryId, pullRequestId, revisionKey, filePath, ct);
        if (diff is null)
        {
            return this.NotFound();
        }

        return this.Ok(
            new RetainedFileDiffDto(
                diff.FilePath,
                diff.RevisionKey,
                diff.ChangeType,
                diff.IsBinary,
                diff.UnifiedDiff,
                diff.CreatedAt));
    }

    // Enforces admin auth and validates the shared route/query parameters. The owning connection is
    // resolved server-side from the retained data, so no connection id is required here. Returns a non-null
    // IActionResult (the error response) when the caller should not proceed.
    private IActionResult? ValidateLookup(Guid clientId, string repositoryId)
    {
        // Retained pull-request data is part of viewing a review, so any caller with at least read
        // (ClientUser) access to the owning client may read it — the same authorization the other
        // review-read endpoints use — while a global admin passes for any client.
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (clientId == Guid.Empty)
        {
            return this.BadRequest(new { error = "clientId is required." });
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return this.BadRequest(new { error = "repositoryId is required." });
        }

        return null;
    }
}
