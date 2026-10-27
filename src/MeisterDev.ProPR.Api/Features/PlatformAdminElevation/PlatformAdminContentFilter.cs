// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace MeisterDev.ProPR.Api.Features.PlatformAdminElevation;

/// <summary>
///     Applies the content rule for platform administrators to every action classified as
///     <see cref="TenantDataAccess.Content" /> or <see cref="TenantDataAccess.Mixed" />.
/// </summary>
/// <remarks>
///     <para>
///         For a content action with a named client, the filter finds the client from the source the classification
///         declares and refuses the request with 403 and the code <c>elevation_required</c> when the platform
///         administrator may not read the content of that client. Tenant users, tenant administrators and tenant
///         machine callers pass through unchanged, because the rule concerns platform administrators only.
///     </para>
///     <para>
///         After the action succeeded, the filter records every read the request registered under an elevation in the
///         audit history of the tenant, before the answer is written. When the record cannot be written, the answer is
///         replaced by 503, so no content reaches a platform administrator without a record.
///     </para>
///     <para>
///         For a platform administrator, the filter answers a content request itself and does not run the action when
///         it cannot establish the client: 400 when the request carries no readable client identifier or names the
///         client more than once, and 404 when the client or the review job does not exist. Otherwise an action that
///         reads its client in another way than the filter could return content without a grant and without a record.
///     </para>
/// </remarks>
public sealed partial class PlatformAdminContentFilter(ILogger<PlatformAdminContentFilter> logger) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var classification = context.ActionDescriptor.EndpointMetadata.OfType<TenantDataAttribute>().LastOrDefault();
        if (classification is not { Access: TenantDataAccess.Content or TenantDataAccess.Mixed } || !PlatformAdminContent.AppliesTo(http))
        {
            await next();
            return;
        }

        var ct = http.RequestAborted;
        if (classification is { Access: TenantDataAccess.Content, Scope: not ContentScopeSource.Action })
        {
            var scope = await ResolveClientAsync(context, classification, ct);
            if (scope.Answer is { } answer)
            {
                context.Result = answer;
                return;
            }

            var grant = (await PlatformAdminContent.ResolveAsync(http, [scope.ClientId!.Value], ct)).FirstOrDefault();
            if (grant is null)
            {
                context.Result = NotFound("The client does not exist.");
                return;
            }

            if (grant.Kind == Application.Features.PlatformAdminElevation.ContentAccessKind.Refused)
            {
                context.Result = PlatformAdminContent.Refusal(grant);
                return;
            }

            PlatformAdminContent.Use(http, grant, classification.Resource, scope.ReviewJobId);
        }

        var executed = await next();
        if (!Succeeded(executed) || !PlatformAdminContent.HasPendingReads(http))
        {
            return;
        }

        try
        {
            await PlatformAdminContent.RecordPendingReadsAsync(http, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadRecordFailed(logger, http.Request.Path, ex);
            executed.Result = new ObjectResult(
                new
                {
                    error = "ProPR could not record this content read in the audit history of the tenant, so the content is withheld. Try again.",
                })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }
    }

    /// <summary>
    ///     Finds the client of a content request from the source its classification declares. A request without exactly
    ///     one readable identifier, or for a review job that does not exist, gets the answer the filter sends in place
    ///     of the action.
    /// </summary>
    private static async Task<ContentScope> ResolveClientAsync(
        ActionExecutingContext context,
        TenantDataAttribute classification,
        CancellationToken ct)
    {
        switch (classification.Scope)
        {
            case ContentScopeSource.ClientRoute:
                return ReadGuid(context.RouteData.Values.GetValueOrDefault("clientId")?.ToString()) is { } routeClientId
                    ? new ContentScope(routeClientId, null, null)
                    : Refuse("The request names no readable client.");
            case ContentScopeSource.ClientQuery:
                // A repeated parameter is refused, because model binding gives the action one of the values and the
                // grant has to cover the client the action reads.
                var values = context.HttpContext.Request.Query["clientId"];
                if (values.Count > 1)
                {
                    return Refuse("Name the client once with clientId.");
                }

                return ReadGuid(values.Count == 1 ? values[0] : null) is { } queryClientId
                    ? new ContentScope(queryClientId, null, null)
                    : Refuse("clientId must be the identifier of a client.");
            case ContentScopeSource.ReviewJob:
                if (ReadGuid(context.RouteData.Values.GetValueOrDefault(classification.RouteValue)?.ToString()) is not { } jobId)
                {
                    return Refuse("The request names no readable review job.");
                }

                return await PlatformAdminContent.GetReviewClientIdAsync(context.HttpContext, jobId, ct) is { } jobClientId
                    ? new ContentScope(jobClientId, jobId, null)
                    : new ContentScope(null, null, NotFound("The review job does not exist."));
            default:
                return Refuse("The request names no readable client.");
        }
    }

    private static ContentScope Refuse(string error) => new(null, null, new BadRequestObjectResult(new { error }));

    private static NotFoundObjectResult NotFound(string error) => new(new { error });

    private static Guid? ReadGuid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;

    private static bool Succeeded(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        return executed.Result switch
        {
            IStatusCodeActionResult { StatusCode: { } status } => status is >= 200 and < 300,
            _ => true,
        };
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ProPR could not record a content read of a platform administrator at {Path}; the answer was withheld")]
    private static partial void LogReadRecordFailed(ILogger logger, PathString path, Exception ex);

    /// <summary>The client of a content request, or the answer the filter sends when it cannot establish one.</summary>
    private sealed record ContentScope(Guid? ClientId, Guid? ReviewJobId, IActionResult? Answer);
}
