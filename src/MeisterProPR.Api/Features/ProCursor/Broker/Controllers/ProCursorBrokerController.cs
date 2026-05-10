// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.ProCursor.Broker.Controllers;

/// <summary>
///     Internal ProPR broker endpoints used by the extracted ProCursor runtime.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = MeisterProPR.Infrastructure.Features.ProCursor.Remote.ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class ProCursorBrokerController(
    IProCursorScmBroker scmBroker,
    IProCursorEmbeddingBroker embeddingBroker) : ControllerBase
{
    /// <summary>
    ///     Materializes repository content for a tracked ProCursor branch.
    /// </summary>
    /// <param name="request">SCM materialization request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The materialized SCM snapshot.</returns>
    /// <response code="200">The repository content was materialized.</response>
    /// <response code="404">The source or branch was not found.</response>
    /// <response code="409">The request conflicts with the current source state.</response>
    /// <response code="503">The upstream ProCursor dependency is unavailable.</response>
    [HttpPost("/internal/propr/procursor/broker/scm/materialize")]
    public async Task<IActionResult> Materialize(
        [FromBody] ProCursorScmMaterializationRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await scmBroker.MaterializeAsync(request.Source, request.TrackedBranch, request.RequestedCommitSha, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Resolves the latest commit SHA for a tracked ProCursor branch.
    /// </summary>
    /// <param name="request">Tracked-branch head request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The latest commit SHA for the tracked branch.</returns>
    /// <response code="200">The latest branch head was resolved.</response>
    /// <response code="404">The source or branch was not found.</response>
    /// <response code="409">The request conflicts with the current source state.</response>
    /// <response code="503">The upstream ProCursor dependency is unavailable.</response>
    [HttpPost("/internal/propr/procursor/broker/scm/branch-head")]
    public async Task<IActionResult> GetBranchHead(
        [FromBody] ProCursorTrackedBranchHeadRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(new ProCursorTrackedBranchHeadResponse(
                await scmBroker.GetLatestCommitShaAsync(request.Source, request.TrackedBranch, ct)));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Resolves the embedding deployment configuration for a client.
    /// </summary>
    /// <param name="request">Embedding deployment request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The embedding deployment configuration.</returns>
    /// <response code="200">The deployment configuration was resolved.</response>
    /// <response code="404">The client or deployment was not found.</response>
    /// <response code="409">The request conflicts with the current embedding configuration.</response>
    /// <response code="503">The upstream ProCursor dependency is unavailable.</response>
    [HttpPost("/internal/propr/procursor/broker/embeddings/deployment")]
    public async Task<IActionResult> GetEmbeddingDeployment(
        [FromBody] ProCursorEmbeddingDeploymentRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await embeddingBroker.GetDeploymentAsync(request.ClientId, request.ExpectedDimensions, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Generates embeddings for a batch of inputs through the ProPR broker.
    /// </summary>
    /// <param name="request">Embedding batch request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The generated embedding batch response.</returns>
    /// <response code="200">Embeddings were generated successfully.</response>
    /// <response code="404">The client or deployment was not found.</response>
    /// <response code="409">The request conflicts with the current embedding configuration.</response>
    /// <response code="503">The upstream ProCursor dependency is unavailable.</response>
    [HttpPost("/internal/propr/procursor/broker/embeddings/generate")]
    public async Task<IActionResult> GenerateEmbeddings(
        [FromBody] ProCursorEmbeddingBatchRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await embeddingBroker.GenerateEmbeddingsAsync(
                request.ClientId,
                request.Inputs,
                request.ExpectedDimensions,
                ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }
}
