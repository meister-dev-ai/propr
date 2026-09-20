// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Starts, continues and reports on the operations a provider family declares against one AI connection.
/// </summary>
/// <remarks>
///     <para>
///         A provider family serves no route of its own: it depends on no web framework, takes no part in this
///         host's authentication, and ships no request pipeline. It declares operations instead, and this is the
///         one path they are reached through.
///     </para>
///     <para>
///         One route template covers both ownership shapes rather than one per shape, because the rule that
///         decides who may act is the connection's owner and not the address the request arrived at. The
///         connection is resolved first, the role its owner requires is derived from it, and a caller without
///         that role is refused before the family is invoked.
///     </para>
///     <para>
///         The family's identity key travels in the request body rather than in the path, because a key carries a
///         separator character.
///     </para>
/// </remarks>
/// <param name="connections">Resolves the connection an action acts on.</param>
/// <param name="dispatcher">Starts and continues the family's work around the host's own checks.</param>
/// <param name="invocations">Reads the record of a run.</param>
[ApiController]
[Route("ai-provider-actions")]
public sealed class AiProviderActionsController(
    IAiConnectionRepository connections,
    ProviderActionDispatcher dispatcher,
    ProviderActionInvocations invocations) : ControllerBase
{
    /// <summary>Starts one declared action against one connection.</summary>
    /// <param name="request">The connection, the provider family and the action.</param>
    /// <param name="ct">Cancels the caller's wait for an answer, not the family's work.</param>
    /// <response code="200">The run that was opened, with the family's answer where one arrived in time.</response>
    /// <response code="400">The family declares no such action, or this build cannot resolve the family.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not hold the role the connection's owner requires.</response>
    /// <response code="404">There is no such connection.</response>
    /// <response code="409">The installation is not licensed for the capability the family declared.</response>
    [HttpPost("dispatch")]
    [ProducesResponseType(typeof(AiProviderActionDispatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Dispatch(
        [FromBody] DispatchProviderActionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await this.ResolveAndAuthorizeAsync(request.ConnectionId, ct);
        if (resolved.Refusal is { } refused)
        {
            return refused;
        }

        var outcome = await dispatcher.DispatchAsync(
            resolved.Connection!,
            request.AddInKey,
            request.ActionId,
            AuthHelpers.GetUserId(this.HttpContext),
            ct);

        return this.Answer(resolved.Connection!, outcome);
    }

    /// <summary>
    ///     Submits the values an action asked for, continuing the run that asked rather than opening a second
    ///     one.
    /// </summary>
    /// <remarks>
    ///     The submitted values are inputs to the run and reach no column, which keeps a pasted callback address
    ///     carrying a live authorization code out of the database. The administrator recorded on the run is the
    ///     one who started it and is unchanged by a submission, whoever makes it: any administrator holding the
    ///     owner role can submit another administrator's callback value, and recording the submitter would point
    ///     a later revocation at the wrong person.
    /// </remarks>
    /// <param name="invocationId">The run the form came from.</param>
    /// <param name="request">The values, by declared input name.</param>
    /// <param name="ct">Cancels the caller's wait for an answer, not the family's work.</param>
    /// <response code="200">The run, with the family's answer where one arrived in time.</response>
    /// <response code="400">The run has already finished or expired, or its action is no longer declared.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not hold the role the connection's owner requires.</response>
    /// <response code="404">There is no such run, or its connection is gone.</response>
    /// <response code="409">The installation is not licensed for the capability the family declared.</response>
    [HttpPost("{invocationId:guid}/values")]
    [ProducesResponseType(typeof(AiProviderActionDispatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitValues(
        Guid invocationId,
        [FromBody] SubmitProviderActionValuesRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invocation = await invocations.GetAsync(invocationId, ct);

        // A run outlives the connection it acted on, so a read of one can find a row whose connection is gone.
        // There is nothing left to continue against, and the caller's role is derived from that connection.
        if (invocation?.ConnectionProfileId is not { } connectionId)
        {
            return this.NotFound();
        }

        var resolved = await this.ResolveAndAuthorizeAsync(connectionId, ct);
        if (resolved.Refusal is { } refused)
        {
            return refused;
        }

        var outcome = await dispatcher.ContinueAsync(
            resolved.Connection!,
            invocation,
            request.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ct);

        return this.Answer(resolved.Connection!, outcome);
    }

    /// <summary>Reports where one run stands.</summary>
    /// <remarks>
    ///     Read by whoever holds the role the dispatch required, which is the connection's owner role and not
    ///     being the administrator who started the run: a run is state on a connection, and an administrator of
    ///     that connection can see what is happening to it.
    /// </remarks>
    /// <param name="invocationId">The run to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <response code="200">The run.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller does not hold the role the connection's owner requires.</response>
    /// <response code="404">There is no such run, or its connection is gone.</response>
    [HttpGet("{invocationId:guid}")]
    [ProducesResponseType(typeof(AiProviderActionInvocationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvocation(Guid invocationId, CancellationToken ct = default)
    {
        var invocation = await invocations.GetAsync(invocationId, ct);

        // The role a caller needs is the one the connection's owner requires, and a run whose connection is gone
        // names nobody to derive it from. Reported as absent rather than read by whoever asks.
        if (invocation?.ConnectionProfileId is not { } connectionId)
        {
            return this.NotFound();
        }

        var resolved = await this.ResolveAndAuthorizeAsync(connectionId, ct);

        return resolved.Refusal ?? this.Ok(ToDto(invocation));
    }

    /// <summary>
    ///     Derives the role a connection's owner requires and refuses a caller who does not hold it.
    /// </summary>
    /// <remarks>
    ///     A client-owned connection needs client administrator on that client, a tenant-owned one needs tenant
    ///     administrator on that tenant, and a connection owned by neither is reachable only by a platform
    ///     administrator. The same rule decides who may dispatch, who may submit values and who may read a run,
    ///     because all three act on the same connection.
    /// </remarks>
    /// <param name="connection">The connection the request acts on.</param>
    /// <param name="context">The current request, carrying the caller's resolved roles.</param>
    internal static IActionResult? AuthorizeConnectionAccess(AiConnectionDto connection, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.ClientId is { } clientId)
        {
            return AuthHelpers.RequireClientRole(context, clientId, ClientRole.ClientAdministrator);
        }

        return connection.TenantId is { } tenantId
            ? AuthHelpers.RequireTenantRole(context, tenantId, TenantRole.TenantAdministrator)
            : AuthHelpers.RequirePlatformAdmin(context);
    }

    private static AiProviderActionInvocationDto ToDto(ProviderActionInvocation invocation)
    {
        return new AiProviderActionInvocationDto(
            invocation.Id,
            invocation.ConnectionProfileId,
            invocation.ConnectionDisplayName,
            invocation.AddInKey,
            invocation.ActionId,
            invocation.State,
            invocation.TerminalMessage,
            invocation.WaitingFor,
            invocation.ExpiresAt);
    }

    private IActionResult Answer(AiConnectionDto connection, ProviderActionDispatchOutcome outcome)
    {
        switch (outcome.Refusal)
        {
            case ProviderDispatchRefusal.None:
                break;
            case ProviderDispatchRefusal.CapabilityUnavailable:
                return this.Conflict(new { error = outcome.Message });
            default:
                return this.BadRequest(new { error = outcome.Message });
        }

        // A refusal was ruled out above, so the dispatcher opened or continued a run and reported where it
        // stands.
        return this.Ok(
            new AiProviderActionDispatchDto(
                ToDto(outcome.Invocation!),
                Describe(connection, outcome.Result)));
    }

    private static AiProviderActionResultDto? Describe(AiConnectionDto connection, ProviderActionResult? result)
    {
        return result switch
        {
            null => null,
            ProviderActionCompleted completed =>
                new AiProviderActionResultDto(AiProviderActionResultKind.Completed, completed.Message),
            ProviderActionFailed failed =>
                new AiProviderActionResultDto(AiProviderActionResultKind.Failed, failed.Message),
            ProviderActionOpenUrl open =>
                new AiProviderActionResultDto(
                    AiProviderActionResultKind.OpenUrl,
                    Message: null,
                    open.Url,
                    open.AwaitCompletion),
            ProviderActionShowForm form =>
                new AiProviderActionResultDto(
                    AiProviderActionResultKind.ShowForm,
                    Message: null,
                    Url: null,
                    AwaitCompletion: false,
                    ProviderDeclaredFieldProjection.Describe(form.Fields, connection.DeclaredSecrets.Values)),
            _ => new AiProviderActionResultDto(
                AiProviderActionResultKind.Failed,
                "The provider family answered with a result this host has no handling for."),
        };
    }

    private async Task<(AiConnectionDto? Connection, IActionResult? Refusal)> ResolveAndAuthorizeAsync(
        Guid connectionId,
        CancellationToken ct)
    {
        if (AuthHelpers.RequireAuthenticated(this.HttpContext) is { } unauthenticated)
        {
            return (null, unauthenticated);
        }

        var connection = await connections.GetByIdAsync(connectionId, ct);
        if (connection is null)
        {
            return (null, this.NotFound());
        }

        var refusal = AuthorizeConnectionAccess(connection, this.HttpContext);

        return refusal is null ? (connection, null) : (null, refusal);
    }
}
