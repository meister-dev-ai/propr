// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.ProCursor.Broker.Controllers;

/// <summary>
///     Internal ProPR broker endpoints for embedding generation, used by the extracted ProCursor runtime.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ProCursorSharedKeyAuthenticationDefaults.Scheme)]
[Route("internal/propr/procursor/broker/embeddings")]
public sealed class ProCursorEmbeddingBrokerController(
    IProCursorEmbeddingBroker embeddingBroker,
    ILogger<ProCursorEmbeddingBrokerController> logger) : ControllerBase
{
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
    [HttpPost("deployment")]
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
            logger.LogWarning(ex, "ProCursor embedding broker request conflicted with the current embedding configuration.");
            return this.Conflict(new { error = "The request conflicts with the current embedding configuration." });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            logger.LogWarning(ex, "ProCursor upstream dependency was unavailable during an embedding broker request.");
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The upstream ProCursor dependency is unavailable." });
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
    [HttpPost("generate")]
    public async Task<IActionResult> GenerateEmbeddings(
        [FromBody] ProCursorEmbeddingBatchRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(
                await embeddingBroker.GenerateEmbeddingsAsync(
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
            logger.LogWarning(ex, "ProCursor embedding broker request conflicted with the current embedding configuration.");
            return this.Conflict(new { error = "The request conflicts with the current embedding configuration." });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            logger.LogWarning(ex, "ProCursor upstream dependency was unavailable during an embedding broker request.");
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The upstream ProCursor dependency is unavailable." });
        }
    }
}
