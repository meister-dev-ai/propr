// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Web;

/// <summary>The answer when a platform administrator asks for content without an elevation that covers it.</summary>
/// <param name="Error">What the administrator needs to do.</param>
/// <param name="Code">Always <c>elevation_required</c>.</param>
/// <param name="TenantId">The tenant of the client whose content was requested.</param>
/// <param name="ClientId">The client whose content was requested.</param>
public sealed record ElevationRequiredResponse(string Error, string Code, Guid TenantId, Guid ClientId);

/// <summary>
///     Applies the rule for platform administrators to the content of tenants: a platform administrator reads the
///     content of a client of the System tenant at any time, and the content of any other client only during an
///     elevation that covers it, also when the administrator holds a role on the client through a membership. Every read
///     under an elevation is recorded in the audit history of the tenant. Callers who are not platform administrators
///     keep the access their roles give them.
/// </summary>
/// <remarks>
///     A content endpoint names its client through <see cref="TenantDataAttribute" />, and a filter of the API applies the
///     rule before the action runs. An endpoint that spans several clients resolves the readable clients here, and
///     registers the grants it used with <see cref="Use(HttpContext, ContentAccessGrant, string, Guid?)" />. The filter
///     records the registered reads after the action succeeded and before the answer is written.
/// </remarks>
public static class PlatformAdminContent
{
    /// <summary>The error code of a refused content read.</summary>
    public const string ElevationRequiredCode = "elevation_required";

    private const string PendingReadsItemKey = "PlatformAdminContentReads";

    /// <summary>Returns <see langword="true" /> when the rule applies to the caller, which is the case for a platform administrator.</summary>
    public static bool AppliesTo(HttpContext ctx) => AuthHelpers.IsAdmin(ctx);

    /// <summary>
    ///     Decides, for each of the given clients that exists, whether the calling platform administrator may read its
    ///     content.
    /// </summary>
    public static async Task<IReadOnlyList<ContentAccessGrant>> ResolveAsync(
        HttpContext ctx,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken ct)
    {
        var service = ctx.RequestServices.GetRequiredService<IPlatformAdminElevationService>();
        return await service.ResolveAsync(AdministratorId(ctx), clientIds, ct);
    }

    /// <summary>Returns every client whose content the calling platform administrator may read.</summary>
    public static async Task<IReadOnlyList<ContentAccessGrant>> ResolveReadableAsync(HttpContext ctx, CancellationToken ct)
    {
        var service = ctx.RequestServices.GetRequiredService<IPlatformAdminElevationService>();
        return await service.ResolveReadableAsync(AdministratorId(ctx), ct);
    }

    /// <summary>Returns the client that owns a review job or a thread pass, or null when neither exists.</summary>
    public static Task<Guid?> GetReviewClientIdAsync(HttpContext ctx, Guid reviewJobOrThreadPassId, CancellationToken ct) =>
        ctx.RequestServices.GetRequiredService<IPlatformAdminElevationService>().GetReviewClientIdAsync(reviewJobOrThreadPassId, ct);

    /// <summary>The 403 answer for a refused grant, which names the tenant and the client an elevation has to cover.</summary>
    public static IActionResult Refusal(ContentAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new ObjectResult(
            new ElevationRequiredResponse(
                "This content belongs to a tenant. A platform administrator reads it only during an elevation that covers the client.",
                ElevationRequiredCode,
                grant.TenantId,
                grant.ClientId))
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    /// <summary>
    ///     Registers that the answer of the current request contains content of the client of <paramref name="grant" />.
    ///     A grant from an elevation is recorded once the action succeeded; other grants need no record.
    /// </summary>
    /// <param name="ctx">The current HTTP context.</param>
    /// <param name="grant">The grant the answer uses.</param>
    /// <param name="resource">What the answer contains, as a noun phrase for the audit record.</param>
    /// <param name="reviewJobId">The review job or thread pass the answer concerns, when it concerns one.</param>
    public static void Use(HttpContext ctx, ContentAccessGrant grant, string resource, Guid? reviewJobId = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant is not { Kind: ContentAccessKind.Elevation, Elevation: not null })
        {
            return;
        }

        if (ctx.Items[PendingReadsItemKey] is not List<PendingRead> pending)
        {
            pending = [];
            ctx.Items[PendingReadsItemKey] = pending;
        }

        pending.Add(new PendingRead(grant, resource, reviewJobId));
    }

    /// <summary>Registers several grants the answer of the current request uses, as <see cref="Use(HttpContext, ContentAccessGrant, string, Guid?)" />.</summary>
    public static void Use(HttpContext ctx, IEnumerable<ContentAccessGrant> grants, string resource)
    {
        ArgumentNullException.ThrowIfNull(grants);
        foreach (var grant in grants)
        {
            Use(ctx, grant, resource);
        }
    }

    /// <summary>Returns <see langword="true" /> when the current request registered reads that still need a record.</summary>
    public static bool HasPendingReads(HttpContext ctx) => ctx.Items[PendingReadsItemKey] is List<PendingRead> { Count: > 0 };

    /// <summary>
    ///     Records the reads the current request registered, one audit entry per elevation, resource and review job,
    ///     and clears them.
    /// </summary>
    public static async Task RecordPendingReadsAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Items[PendingReadsItemKey] is not List<PendingRead> { Count: > 0 } pending)
        {
            return;
        }

        var request = $"{ctx.Request.Method} {ctx.Request.PathBase}{ctx.Request.Path}";
        var reads = pending
            .GroupBy(read => (read.Grant.Elevation!.Id, read.Resource, read.ReviewJobId))
            .Select(group => new ContentRead(
                group.First().Grant.Elevation!,
                group.Select(read => read.Grant).DistinctBy(grant => grant.ClientId).ToList(),
                group.Key.Resource,
                request,
                group.Key.ReviewJobId))
            .ToList();

        var service = ctx.RequestServices.GetRequiredService<IPlatformAdminElevationService>();
        await service.RecordReadsAsync(AdministratorId(ctx), reads, ct);
        pending.Clear();
    }

    // A platform administrator token always carries the user. Without one no elevation can match, so the content of
    // every tenant other than the System tenant stays refused.
    private static Guid AdministratorId(HttpContext ctx) => AuthHelpers.GetUserId(ctx) ?? Guid.Empty;

    private sealed record PendingRead(ContentAccessGrant Grant, string Resource, Guid? ReviewJobId);
}
