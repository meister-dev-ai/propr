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
