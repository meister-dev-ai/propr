// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.ProCursor;

/// <summary>
///     The answer of the ProCursor endpoints of a client while the installation runs without ProCursor.
/// </summary>
/// <remarks>
///     ProPR answers <c>404</c> with a problem details body whose <c>code</c> is <c>procursor_not_configured</c>. The code
///     separates the missing service from a missing knowledge source, which ProPR also answers with <c>404</c>. The status
///     is a client error status, because the configuration of the installation leaves ProCursor out and no service failed.
/// </remarks>
internal static class ProCursorNotConfigured
{
    /// <summary>The code of the answer, which a caller can act on without reading the title.</summary>
    public const string Code = "procursor_not_configured";

    /// <summary>The title of the answer.</summary>
    public const string Title = "ProCursor is not configured in this installation.";

    /// <summary>Creates the answer to the current request of a controller.</summary>
    /// <param name="controller">The controller that answers the request.</param>
    /// <returns>
    ///     The answer as JSON with the content type <c>application/problem+json</c>, whatever content type the request
    ///     accepts. The CSV export answers with the same JSON.
    /// </returns>
    public static JsonResult Answer(ControllerBase controller)
    {
        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            StatusCodes.Status404NotFound,
            Title);
        problem.Extensions["code"] = Code;
        return new JsonResult(problem)
        {
            StatusCode = StatusCodes.Status404NotFound,
            ContentType = "application/problem+json",
        };
    }
}
