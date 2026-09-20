// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One operation an operator can start against a connection, described so a console can offer it without
///     knowing anything about the provider family that declared it.
/// </summary>
/// <param name="AddInKey">
///     The provider family that declared it. Carried beside the action so a console sends back the key the
///     server reported for this connection rather than composing one, which it could not: a key is the family's
///     own declared constant.
/// </param>
/// <param name="Id">What a dispatch request names the action by.</param>
/// <param name="Label">What an operator sees on the affordance that starts it.</param>
/// <param name="Inputs">
///     The values the action collects from the operator. They belong to one run and reach no column, so a pasted
///     address carrying a live authorization code is never stored.
/// </param>
/// <param name="CoLocationNotice">
///     What an operator has to know before starting it, composed by the host from what the family declared and
///     how the connection is configured, or null when the family states no requirement the deployment has to
///     meet.
/// </param>
public sealed record AiDeclaredActionDto(
    string AddInKey,
    string Id,
    string Label,
    IReadOnlyList<AiDeclaredFieldDto> Inputs,
    string? CoLocationNotice = null);

/// <summary>Which of the four answers an action gave.</summary>
public enum AiProviderActionResultKind
{
    /// <summary>The action finished, with something to tell the operator.</summary>
    Completed = 0,

    /// <summary>The operator has to visit an address for the action to continue.</summary>
    OpenUrl = 1,

    /// <summary>The action needs values from the operator before it can continue.</summary>
    ShowForm = 2,

    /// <summary>The action cannot be completed.</summary>
    Failed = 3,
}

/// <summary>
///     What a provider family answered a dispatch with, after the host capped and scrubbed its strings and
///     checked any address it asked to have opened.
/// </summary>
/// <param name="Kind">Which of the four answers it is.</param>
/// <param name="Message">What to show the operator, for an answer that carries one.</param>
/// <param name="Url">
///     Where to send the operator, for an answer that carries one. Already checked against the scheme and the
///     family's declared hosts, so an address that reaches here is one the host permits.
/// </param>
/// <param name="AwaitCompletion">
///     Whether the run stays open after the operator has been sent, because the family reports its own terminal
///     state later.
/// </param>
/// <param name="Fields">The values to ask the operator for, for an answer that asks for some.</param>
public sealed record AiProviderActionResultDto(
    AiProviderActionResultKind Kind,
    string? Message = null,
    string? Url = null,
    bool AwaitCompletion = false,
    IReadOnlyList<AiDeclaredFieldDto>? Fields = null);

/// <summary>
///     One run of a declared action, as an operator's view reads it.
/// </summary>
/// <remarks>
///     A dispatch returns before the family's work finishes, so this is what says where the run stands. It is
///     polled while the state is pending and read once more when it is not.
/// </remarks>
/// <param name="Id">The run.</param>
/// <param name="ConnectionId">The connection it acts on, or null once that connection has been deleted.</param>
/// <param name="ConnectionName">The connection's name as it stood when the run was opened.</param>
/// <param name="AddInKey">The provider family whose action it is.</param>
/// <param name="ActionId">The action, as that family declared it.</param>
/// <param name="State">Pending, completed, failed or expired.</param>
/// <param name="Message">
///     What the family said when it finished, capped and scrubbed by the host. For a run that expired, this is
///     what the host recorded it was waiting for.
/// </param>
/// <param name="WaitingFor">What the run is waiting for, recorded by the host when it opened the run.</param>
/// <param name="ExpiresAt">When the window closes on it.</param>
public sealed record AiProviderActionInvocationDto(
    Guid Id,
    Guid? ConnectionId,
    string ConnectionName,
    string AddInKey,
    string ActionId,
    string State,
    string? Message,
    string? WaitingFor,
    DateTimeOffset ExpiresAt);

/// <summary>
///     What a dispatch answered with: the run it opened or continued, and the family's result where one arrived
///     in time.
/// </summary>
/// <param name="Invocation">The run, which the console polls from here on.</param>
/// <param name="Result">
///     What the family answered, or null when it had not answered by the time the request returned. A null result
///     on a pending run is the ordinary case for a flow that outlives the request.
/// </param>
public sealed record AiProviderActionDispatchDto(
    AiProviderActionInvocationDto Invocation,
    AiProviderActionResultDto? Result);

/// <summary>Starts one declared action against one connection.</summary>
/// <param name="ConnectionId">The connection the action acts on.</param>
/// <param name="AddInKey">
///     The provider family whose action it is. Carried as a field rather than a path segment because an identity
///     key contains a separator character.
/// </param>
/// <param name="ActionId">The action, as that family declared it.</param>
public sealed record DispatchProviderActionRequest(Guid ConnectionId, string AddInKey, string ActionId);

/// <summary>Submits the values an action asked for, continuing the run that asked.</summary>
/// <param name="Values">The values by declared input name. They belong to the run and reach no column.</param>
public sealed record SubmitProviderActionValuesRequest(IReadOnlyDictionary<string, string> Values);
