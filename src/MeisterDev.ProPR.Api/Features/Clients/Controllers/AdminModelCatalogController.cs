// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

/// <summary>
///     Refreshing the global model catalog. This is how a running installation moves to newer model data without a
///     redeploy: an operator uploads a snapshot rather than the application fetching one, so no outbound request
///     to a catalog host is ever made.
/// </summary>
/// <remarks>
///     Restricted to platform administrators because an import writes the global rows every tenant reads. Tenant
///     administrators shape their own view through overrides instead, which cannot affect anybody else.
/// </remarks>
[ApiController]
[Route("admin/model-catalog")]
public sealed partial class AdminModelCatalogController(
    IModelCatalogImportService catalogImport,
    ILogger<AdminModelCatalogController> logger) : ControllerBase
{
    /// <summary>Imports an uploaded catalog snapshot, replacing the global entries it describes.</summary>
    /// <param name="snapshot">The snapshot file, in the format the configured importer understands.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The import completed, reporting how many global entries were written.</response>
    /// <response code="400">No file was supplied, or its content could not be read as a snapshot.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    [HttpPost("snapshot")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    [ProducesResponseType(typeof(ModelCatalogImportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ImportSnapshot(IFormFile? snapshot, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (snapshot is null || snapshot.Length == 0)
        {
            this.ModelState.AddModelError(nameof(snapshot), "A snapshot file is required.");
            return this.ValidationProblem();
        }

        await using var content = snapshot.OpenReadStream();

        int written;
        try
        {
            written = await catalogImport.ImportSnapshotAsync(content, ct);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            // A malformed upload is operator error, not a server fault, and the cause belongs in the response
            // rather than only in the log.
            this.ModelState.AddModelError(nameof(snapshot), $"The snapshot could not be read: {exception.Message}");
            return this.ValidationProblem();
        }

        LogSnapshotImported(logger, written);
        return this.Ok(new ModelCatalogImportResponse(written));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Model catalog snapshot imported. {EntryCount} global entries written.")]
    private static partial void LogSnapshotImported(ILogger logger, int entryCount);
}

/// <summary>Outcome of a catalog snapshot import.</summary>
/// <param name="EntriesWritten">How many global catalog entries were written or updated.</param>
public sealed record ModelCatalogImportResponse(int EntriesWritten);
