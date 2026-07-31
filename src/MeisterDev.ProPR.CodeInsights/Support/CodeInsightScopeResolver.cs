// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Http;

namespace MeisterDev.ProPR.CodeInsights.Support;

/// <summary>
///     The resolved client scope for one code-insight read, or the response that denies it.
/// </summary>
/// <param name="ClientIds">The clients this caller may aggregate over. Empty means aggregate over nothing.</param>
/// <param name="Denied">The response to return instead, when the caller may not read at all.</param>
public readonly record struct CodeInsightScope(IReadOnlyList<Guid> ClientIds, IActionResult? Denied);

/// <summary>
///     Decides which clients a code-insight read may aggregate over.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The authorised set is derived from the caller, never from the request.</strong> A cross-client
///         aggregate over a set a request supplied would be an exfiltration primitive: name someone else's client
///         and receive their numbers. A <c>clientId</c> parameter therefore only ever <em>narrows</em> what the
///         caller may already see, and asking for a client outside that set is a denial rather than an empty
///         result: an empty result would hide an authorisation failure behind what looks like missing data.
///     </para>
///     <para>
///         Two audiences, two rules, one place. The code-quality views answer "what does this codebase keep
///         getting wrong", so client access is the right bar. The reviewer-performance views judge ProPR itself
///         from AI-estimated evidence, so they sit with the other operator surfaces behind tenant administration.
///         Both also require the licence, and both fail closed when the edition cannot be established.
///     </para>
/// </remarks>
public sealed class CodeInsightScopeResolver(
    IClientAdminService clientAdminService,
    ILicensingCapabilityService? licensingCapabilityService = null)
{
    /// <summary>
    ///     Resolves the scope for a read any client user may perform: the clients the caller holds a role on, or
    ///     every client for a platform administrator.
    /// </summary>
    public async Task<CodeInsightScope> ResolveForClientAccessAsync(
        HttpContext httpContext,
        Guid? requestedClientId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (await this.RefuseUnauthenticatedOrUnlicensedAsync(httpContext, ct) is { } refusal)
        {
            return refusal;
        }

        if (AuthHelpers.IsAdmin(httpContext))
        {
            return await this.ResolveForAdministratorAsync(requestedClientId, ct);
        }

        if (AuthHelpers.RequireAnyClientRole(httpContext, ClientRole.ClientUser) is { } roleCheck)
        {
            return new CodeInsightScope([], roleCheck);
        }

        return Narrow(AuthHelpers.GetClientRoles(httpContext).Keys.ToList(), requestedClientId);
    }

    /// <summary>
    ///     Resolves the scope for a read reserved to operators: every client for a platform administrator, and the
    ///     clients of the tenants the caller administers otherwise.
    /// </summary>
    /// <remarks>
    ///     Client roles are deliberately not consulted. A tenant administrator may hold no client role at all, and
    ///     a client administrator holding several roles is still not the person who decides whether the reviewer is
    ///     earning its keep.
    /// </remarks>
    public async Task<CodeInsightScope> ResolveForTenantAdministrationAsync(
        HttpContext httpContext,
        Guid? requestedClientId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (await this.RefuseUnauthenticatedOrUnlicensedAsync(httpContext, ct) is { } refusal)
        {
            return refusal;
        }

        if (AuthHelpers.IsAdmin(httpContext))
        {
            return await this.ResolveForAdministratorAsync(requestedClientId, ct);
        }

        var administeredTenants = AuthHelpers.GetTenantRoles(httpContext)
            .Where(entry => entry.Value >= TenantRole.TenantAdministrator)
            .Select(entry => entry.Key)
            .ToHashSet();

        if (administeredTenants.Count == 0)
        {
            return new CodeInsightScope([], Forbid("Reviewer performance requires tenant administration."));
        }

        var clients = await clientAdminService.GetAllAsync(ct);
        var owned = clients
            .Where(client => client.TenantId is not null && administeredTenants.Contains(client.TenantId.Value))
            .Select(client => client.Id)
            .ToList();

        return Narrow(owned, requestedClientId);
    }

    /// <summary>Applies the caller's optional narrowing, refusing anything outside the authorised set.</summary>
    private static CodeInsightScope Narrow(IReadOnlyList<Guid> authorised, Guid? requestedClientId)
    {
        if (requestedClientId is null)
        {
            return new CodeInsightScope(authorised, null);
        }

        return authorised.Contains(requestedClientId.Value)
            ? new CodeInsightScope([requestedClientId.Value], null)
            : new CodeInsightScope([], Forbid("Access denied for this client."));
    }

    private static ObjectResult Forbid(string message)
    {
        return new ObjectResult(new { error = message }) { StatusCode = StatusCodes.Status403Forbidden };
    }

    private async Task<CodeInsightScope> ResolveForAdministratorAsync(Guid? requestedClientId, CancellationToken ct)
    {
        if (requestedClientId is not null)
        {
            return new CodeInsightScope([requestedClientId.Value], null);
        }

        var all = await clientAdminService.GetAllAsync(ct);
        return new CodeInsightScope(all.Select(client => client.Id).ToList(), null);
    }

    /// <summary>Returns a refusal when the caller is unauthenticated or the installation is not licensed.</summary>
    private async Task<CodeInsightScope?> RefuseUnauthenticatedOrUnlicensedAsync(
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireAuthenticated(httpContext) is { } authenticated)
        {
            return new CodeInsightScope([], authenticated);
        }

        return await this.IsLicensedAsync(ct)
            ? null
            : new CodeInsightScope([], Forbid("Code Insights requires a commercial licence."));
    }

    private async Task<bool> IsLicensedAsync(CancellationToken ct)
    {
        // Fails closed, like the collection gate: an edition that cannot be established serves nothing.
        if (licensingCapabilityService is null)
        {
            return false;
        }

        try
        {
            return await licensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.CodeInsights, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
