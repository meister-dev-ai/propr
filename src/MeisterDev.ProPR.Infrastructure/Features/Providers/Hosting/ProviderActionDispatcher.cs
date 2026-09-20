// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>Why the host would not start or continue an action at all.</summary>
public enum ProviderDispatchRefusal
{
    /// <summary>Nothing was refused.</summary>
    None = 0,

    /// <summary>This build has no provider family for the connection, or not the one the request named.</summary>
    UnknownFamily = 1,

    /// <summary>The family serving the connection declares no action by that identifier.</summary>
    UnknownAction = 2,

    /// <summary>The installation is not licensed for the capability the family declared.</summary>
    CapabilityUnavailable = 3,

    /// <summary>The invocation the request named has already reached a terminal state.</summary>
    InvocationNotPending = 4,

    /// <summary>The submitted values do not match the inputs the action declared.</summary>
    InvalidInput = 5,

    /// <summary>The invocation the request named belongs to a different connection.</summary>
    InvocationNotOnThisConnection = 6,
}

/// <summary>
///     What the host has to say after starting or continuing an action.
/// </summary>
/// <param name="Refusal">Why nothing was started, or <see cref="ProviderDispatchRefusal.None" />.</param>
/// <param name="Message">What the refusal was, in terms an operator can act on.</param>
/// <param name="Invocation">The run, present unless the request was refused before one was opened.</param>
/// <param name="Result">
///     What the family answered, or null when it had not answered by the time the call returned. A null result
///     with a pending run is the ordinary case for a family whose work outlives the call: the operator's view
///     reads the run from then on.
/// </param>
public sealed record ProviderActionDispatchOutcome(
    ProviderDispatchRefusal Refusal,
    string? Message,
    ProviderActionInvocation? Invocation,
    ProviderActionResult? Result);

/// <summary>
///     Starts and continues the operations a provider family declared, on behalf of an operator.
/// </summary>
/// <remarks>
///     <para>
///         The caller has already resolved the connection and refused a caller without the role its owner
///         requires. What is left is what the host does around a family: check that the family declares the
///         action, check that the installation is licensed for the family, open a bounded run, invoke the family
///         with a context bound to that one connection, and honour the result without knowing why it was
///         returned.
///     </para>
///     <para>
///         The call does not wait for the family's work. An operator signing in at a vendor takes minutes, and
///         the answer arrives on a socket the family opened rather than on the call that started it. The call
///         waits as long as the host waits for an answer and then returns, leaving the run open for the family to
///         report against and for the operator's view to read.
///     </para>
///     <para>
///         Containment is what the mechanism allows. A family that throws is refused with the failure recorded
///         against the run. A family that observes its signal returns when the window closes. A family that
///         blocks a thread without observing it is not cut: the run expires, the call returns, and nothing holds
///         the operator's view, which leaves the blocked thread with the family that blocked it.
///     </para>
/// </remarks>
/// <param name="drivers">Resolves the family serving a connection.</param>
/// <param name="invocations">Where a run is opened, read and closed.</param>
/// <param name="cancellations">The signals in-flight runs watch.</param>
/// <param name="contexts">Builds what a family may do against one connection and one run.</param>
/// <param name="capabilities">The licence check made before an action of a family is started.</param>
/// <param name="ownerRoles">Names the administrator recorded as the owner of whatever the action stores.</param>
/// <param name="timeProvider">Supplies the instant windows are judged against.</param>
/// <param name="logger">Records what was started and how it ended.</param>
/// <param name="dispatchWait">
///     How long the call waits for an answer before returning and leaving the run open, or null for the host's
///     own wait.
/// </param>
public sealed partial class ProviderActionDispatcher(
    IAiProviderDriverRegistry drivers,
    ProviderActionInvocations invocations,
    ProviderInvocationCancellation cancellations,
    ProviderConnectionContextFactory contexts,
    IProviderAddInCapabilityGate capabilities,
    IProviderConnectionOwnerRoles ownerRoles,
    TimeProvider timeProvider,
    ILogger<ProviderActionDispatcher> logger,
    TimeSpan? dispatchWait = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoInputs =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How long this dispatcher waits for an answer before returning.</summary>
    private TimeSpan DispatchWait => dispatchWait ?? ProviderHostLimits.DispatchWait;

    /// <summary>Starts one declared action against one connection, opening a run for it.</summary>
    /// <param name="connection">The connection the action acts on, already resolved and authorized.</param>
    /// <param name="addInKey">The family the caller named, checked against the one serving the connection.</param>
    /// <param name="actionId">The action, as the family declared it.</param>
    /// <param name="initiatingAdminId">
    ///     The administrator starting it. Recorded as the owner of whatever credential the action produces, and
    ///     re-checked before that credential is stored.
    /// </param>
    /// <param name="ct">Cancels the caller's wait for an answer, not the family's work.</param>
    public async Task<ProviderActionDispatchOutcome> DispatchAsync(
        AiConnectionDto connection,
        string? addInKey,
        string actionId,
        Guid? initiatingAdminId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var resolution = this.Resolve(connection, addInKey, actionId);
        if (resolution.Refusal is { } refused)
        {
            return refused;
        }

        var (driver, declaration, action, binding) = resolution.Resolved!.Value;

        if (!await capabilities.IsAvailableAsync(declaration.RequiredCapabilityKey, ct).ConfigureAwait(false))
        {
            return Unlicensed(declaration, invocation: null);
        }

        var invocation = await invocations.OpenAsync(
                binding,
                action.Id,
                initiatingAdminId,
                WindowFor(declaration, connection),
                ProviderActionNotices.WaitingFor(declaration, action, connection.ProviderSettings),
                ct)
            .ConfigureAwait(false);

        LogActionStarted(logger, declaration.Key, action.Id, connection.Id, invocation.Id);

        return await this.RunAsync(connection, binding, driver, declaration, action, invocation, NoInputs, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Continues a run with the values the operator submitted for the form it asked for.
    /// </summary>
    /// <remarks>
    ///     The run is the one the form came from. A second run would leave the first pending until its window
    ///     closed, state the deployment requirement again, and let a family that reads a fresh dispatch as a
    ///     fresh start open a second authorization at the vendor while the first is still in flight. The
    ///     initiating administrator and the window belong to the run and are unchanged by a submission, whoever
    ///     made it. The submitted values are inputs to this run and reach no column.
    /// </remarks>
    /// <param name="connection">The connection the run acts on, already resolved and authorized.</param>
    /// <param name="invocation">The run the form came from.</param>
    /// <param name="values">The values the operator submitted, by declared input name.</param>
    /// <param name="ct">Cancels the caller's wait for an answer, not the family's work.</param>
    public async Task<ProviderActionDispatchOutcome> ContinueAsync(
        AiConnectionDto connection,
        ProviderActionInvocation invocation,
        IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(values);

        if (invocation.IsTerminal)
        {
            return new ProviderActionDispatchOutcome(
                ProviderDispatchRefusal.InvocationNotPending,
                invocation.State == ProviderInvocationState.Expired
                    ? "This action ran out of its window before the values were submitted, so there is nothing "
                      + "left to continue. Start it again."
                    : "This action has already finished, so there is nothing left to continue.",
                invocation,
                Result: null);
        }

        // The family, the licence and the context all come from the connection the caller resolved, while the
        // run and its window come from the invocation. A pair that does not belong together would run one
        // connection's family against another's state, so the two are required to name the same connection.
        if (invocation.ConnectionProfileId != connection.Id)
        {
            return new ProviderActionDispatchOutcome(
                ProviderDispatchRefusal.InvocationNotOnThisConnection,
                "This action was started on a different connection, so it cannot be continued here.",
                invocation,
                Result: null);
        }

        var resolution = this.Resolve(connection, invocation.AddInKey, invocation.ActionId);
        if (resolution.Refusal is { } refused)
        {
            return refused with { Invocation = invocation };
        }

        var (driver, declaration, action, binding) = resolution.Resolved!.Value;

        if (!await capabilities.IsAvailableAsync(declaration.RequiredCapabilityKey, ct).ConfigureAwait(false))
        {
            return Unlicensed(declaration, invocation);
        }

        var submitted = DeclaredInputs(declaration, action, values);
        if (submitted.Refusal is { } invalid)
        {
            return new ProviderActionDispatchOutcome(
                ProviderDispatchRefusal.InvalidInput,
                invalid,
                invocation,
                Result: null);
        }

        return await this.RunAsync(connection, binding, driver, declaration, action, invocation, submitted.Values, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     How long a run of this family may stay open, derived from what the family declared and bounded by what
    ///     the host allows.
    /// </summary>
    /// <remarks>
    ///     The family is the party that knows how long its own flow legitimately takes, so the window comes from
    ///     its declaration rather than from one number applied to every family. Where the declaration names a
    ///     configured field holding the window in seconds, the connection's value is read and the declared
    ///     maximum is the ceiling over it.
    /// </remarks>
    /// <param name="declaration">The family whose action is being started.</param>
    /// <param name="connection">The connection it runs against.</param>
    internal static TimeSpan WindowFor(ProviderDeclaration declaration, AiConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(connection);

        if (declaration.InvocationWindow is not { } declared)
        {
            return ProviderHostLimits.MaximumInvocationWindow;
        }

        if (declared.DerivedFromFieldName is not { } fieldName
            || connection.ProviderSettings?.GetValueOrDefault(fieldName) is not { } configured
            || !int.TryParse(configured, out var seconds)
            || seconds <= 0)
        {
            return declared.Maximum;
        }

        var derived = TimeSpan.FromSeconds(seconds);

        return derived < declared.Maximum ? derived : declared.Maximum;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provider add-in action started: {AddInKey} {ActionId} on connection {ConnectionId} as invocation {InvocationId}")]
    private static partial void LogActionStarted(
        ILogger logger,
        string addInKey,
        string actionId,
        Guid connectionId,
        Guid invocationId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provider add-in action {ActionId} of {AddInKey} on invocation {InvocationId} stands at {State}")]
    private static partial void LogActionEnded(
        ILogger logger,
        string addInKey,
        string actionId,
        Guid invocationId,
        string state);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Provider add-in action {ActionId} of {AddInKey} on invocation {InvocationId} threw")]
    private static partial void LogActionThrew(
        ILogger logger,
        Exception exception,
        string addInKey,
        string actionId,
        Guid invocationId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The host failed to prepare provider add-in action {ActionId} of {AddInKey} for invocation {InvocationId}; the run is closed as failed")]
    private static partial void LogActionSetupThrew(
        ILogger logger,
        Exception exception,
        string addInKey,
        string actionId,
        Guid invocationId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "The request that opened invocation {InvocationId} for provider add-in action {ActionId} of {AddInKey} was abandoned before the action started; the run is closed as failed")]
    private static partial void LogActionAbandonedDuringSetup(
        ILogger logger,
        string addInKey,
        string actionId,
        Guid invocationId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "Provider add-in action {ActionId} of {AddInKey} could not be prepared: the connection's stored settings no longer compose an endpoint this family accepts")]
    private static partial void LogActionSetupFailed(
        ILogger logger,
        string addInKey,
        string actionId,
        Exception failure);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Provider add-in action {ActionId} of {AddInKey} had not answered invocation {InvocationId} within the dispatch wait; the run stays open")]
    private static partial void LogActionStillRunning(
        ILogger logger,
        string addInKey,
        string actionId,
        Guid invocationId);

    private static ProviderActionDispatchOutcome Unlicensed(
        ProviderDeclaration declaration,
        ProviderActionInvocation? invocation)
    {
        return new ProviderActionDispatchOutcome(
            ProviderDispatchRefusal.CapabilityUnavailable,
            $"The provider family '{declaration.Label}' requires the '{declaration.RequiredCapabilityKey}' "
            + "capability, which this installation's licence does not currently make available.",
            invocation,
            Result: null);
    }

    /// <summary>
    ///     Puts a family's answer through the checks the host applies to everything a family returns.
    /// </summary>
    /// <remarks>
    ///     Every string is untrusted output and every address is untrusted input to an operator's browser. Both
    ///     are answered here, once, rather than at each place a result is later rendered.
    /// </remarks>
    private static async Task<ProviderActionResult> CheckedAsync(
        ProviderDeclaration declaration,
        ProviderEndpoint endpoint,
        ProviderActionResult result,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets)
    {
        return result switch
        {
            ProviderActionCompleted completed =>
                ProviderActionResult.Completed(await ProviderReportedMessage.ScrubbedAsync(completed.Message, secrets).ConfigureAwait(false)),
            ProviderActionFailed failed =>
                ProviderActionResult.Failed(await ProviderReportedMessage.ScrubbedAsync(failed.Message, secrets).ConfigureAwait(false)),
            ProviderActionShowForm form => form,
            ProviderActionOpenUrl open =>
                ProviderBrowserUrlPolicy.GetRefusalReason(
                    declaration,
                    open.Url,
                    declaration.ListenerPortsIn(endpoint.DeclaredValues)) is { } refusal
                    ? ProviderActionResult.Failed(refusal)
                    : await WithoutACredentialAsync(open, secrets).ConfigureAwait(false),
            _ => ProviderActionResult.Failed("The provider family answered with a result this host has no handling for."),
        };
    }

    /// <summary>
    ///     The address to send the operator to, or a refusal when it carries a credential this connection holds.
    /// </summary>
    /// <remarks>
    ///     The host check above answers where the operator may be sent. It says nothing about what travels in
    ///     the address, and a family composing one from its own configuration can put a stored credential or a
    ///     submitted secret into the query. The operator's browser would then carry it, and so would their
    ///     history and any proxy between them. Refused rather than redacted: an address with a credential taken
    ///     out of it is not an address the provider will honour.
    /// </remarks>
    /// <param name="open">The result the family returned.</param>
    /// <param name="secrets">The credential values this connection holds, read on demand.</param>
    private static async Task<ProviderActionResult> WithoutACredentialAsync(
        ProviderActionOpenUrl open,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets)
    {
        var held = await secrets(CancellationToken.None).ConfigureAwait(false);
        var carries = held.Any(secret =>
            !string.IsNullOrWhiteSpace(secret) && open.Url.Contains(secret, StringComparison.Ordinal));

        return carries
            ? ProviderActionResult.Failed(
                "The provider family answered with an address carrying this connection's credential. It was not "
                + "opened, because the address would put the credential in the browser's history.")
            : open;
    }

    /// <summary>The secret-marked values the operator submitted for this run.</summary>
    /// <remarks>
    ///     A submitted input is credential material as much as a stored one: what arrives this way is a pasted
    ///     callback address carrying a live authorization code, or whatever else the family declared as a secret
    ///     input. It is handed to the family, so it is one quoting position away from coming back in the message
    ///     the family answers with, and that message reaches an operator's page and the invocation row. Nothing
    ///     persists it, so the only place it can be scrubbed against is here, where it is still in hand.
    /// </remarks>
    /// <param name="action">The action, which declares which of its inputs are secret.</param>
    /// <param name="inputs">The values the operator submitted, by declared input name.</param>
    /// <summary>
    ///     The submitted values reduced to the inputs the action declares, or the reason the submission is
    ///     refused.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A family reading an action input is owed the same two promises it gets for a connection field: the
    ///         value is one it declared, and it holds the shape it declared. Neither is checked by the transport,
    ///         because the values arrive as a free-form map on the request.
    ///     </para>
    ///     <para>
    ///         An undeclared name is refused rather than dropped. The host scrubs a family's output of the
    ///         secret-marked inputs the declaration names, so a credential submitted under a name the declaration
    ///         does not carry would reach the family and come back through a result the host has no reason to
    ///         redact. Refusing also tells an operator that a value they believe is in use is read by nothing.
    ///     </para>
    ///     <para>
    ///         A field hidden by its visibility condition is neither required nor forwarded, which is how the
    ///         connection form already treats one: a form that does not show a field cannot have asked about it.
    ///     </para>
    /// </remarks>
    /// <param name="declaration">The family the action belongs to, named in the refusal.</param>
    /// <param name="action">The action whose inputs the values are checked against.</param>
    /// <param name="values">The values the operator submitted, by declared input name.</param>
    private static (IReadOnlyDictionary<string, string> Values, string? Refusal) DeclaredInputs(
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var name in values.Keys)
        {
            if (!action.Inputs.Any(input => string.Equals(input.Name, name, StringComparison.Ordinal)))
            {
                return (NoInputs, $"The '{action.Label}' action of '{declaration.Label}' asks for no value named '{name}'.");
            }
        }

        var visible = VisibleInputs(action.Inputs, values);
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in action.Inputs)
        {
            if (!visible.Contains(input.Name))
            {
                continue;
            }

            var entered = values.TryGetValue(input.Name, out var value) ? value : input.DefaultValue;
            if (string.IsNullOrWhiteSpace(entered))
            {
                if (input.IsRequired)
                {
                    return (NoInputs, $"{input.Label} is required.");
                }

                continue;
            }

            if (ProviderFieldRules.DescribeShapeRefusal(input, entered) is { } shapeRefusal)
            {
                return (NoInputs, shapeRefusal);
            }

            accepted[input.Name] = entered;
        }

        return (accepted, null);
    }

    /// <summary>The names of the action inputs whose visibility condition the submission meets.</summary>
    /// <remarks>
    ///     Read against the submission alone, with the family's defaults behind it. An action input is not
    ///     persisted, so there is no stored value for a deciding field to fall back to.
    /// </remarks>
    /// <param name="inputs">The action's declared inputs.</param>
    /// <param name="values">The values the operator submitted.</param>
    private static IReadOnlySet<string> VisibleInputs(
        IReadOnlyList<ProviderDeclaredField> inputs,
        IReadOnlyDictionary<string, string> values)
    {
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (values.TryGetValue(input.Name, out var entered) && !(input.IsSecret && string.IsNullOrWhiteSpace(entered)))
            {
                effective[input.Name] = entered;
            }
            else if (input.DefaultValue is not null)
            {
                effective[input.Name] = input.DefaultValue;
            }
        }

        return inputs
            .Where(input => ProviderFieldRules.IsVisible(input, effective, inputs))
            .Select(input => input.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyCollection<string> SecretInputsOf(
        ProviderDeclaredAction action,
        IReadOnlyDictionary<string, string> inputs)
    {
        return
        [
            .. action.Inputs
                .Where(input => input.IsSecret)
                .Select(input => inputs.GetValueOrDefault(input.Name))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!),
        ];
    }

    /// <summary>
    ///     The connection's stored credential together with the secret-marked values this submission carried, as
    ///     one set for the message scrub to elide.
    /// </summary>
    /// <param name="stored">Reads the connection's credential when the scrub asks for it.</param>
    /// <param name="submitted">The secret-marked action inputs this submission carried.</param>
    internal static Func<CancellationToken, Task<IReadOnlyCollection<string>>> Combined(
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> stored,
        IReadOnlyCollection<string> submitted)
    {
        if (submitted.Count == 0)
        {
            return stored;
        }

        return async ct =>
        {
            // The submitted values are in hand and the stored ones are read from the database. A read that fails
            // does not take the values the host is already holding with it: a message echoing a pasted
            // authorization code is one of the cases the scrub exists for.
            try
            {
                return [.. await stored(ct).ConfigureAwait(false), .. submitted];
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return submitted;
            }
        };
    }

    private static string? TerminalStateOf(ProviderActionResult result)
    {
        return result switch
        {
            ProviderActionCompleted => ProviderInvocationState.Completed,
            ProviderActionFailed => ProviderInvocationState.Failed,

            // The operator has been sent somewhere and the family says it will report when the flow comes back,
            // so the run stays open for it. A family that does not say so has nothing further to report.
            ProviderActionOpenUrl open => open.AwaitCompletion ? null : ProviderInvocationState.Completed,

            // The run waits for the operator to submit the form, and that submission continues this same run.
            _ => null,
        };
    }

    private static string MessageOf(ProviderActionResult result, ProviderDeclaration declaration)
    {
        return result switch
        {
            ProviderActionCompleted completed => completed.Message,
            ProviderActionFailed failed => failed.Message,
            ProviderActionOpenUrl open => Uri.TryCreate(open.Url, UriKind.Absolute, out var destination)
                ? $"The operator was sent to '{destination.Host}'."
                : "The operator was sent to the provider.",
            _ => $"The '{declaration.Label}' provider family finished the action.",
        };
    }

    private ProviderActionResolution Resolve(AiConnectionDto connection, string? addInKey, string actionId)
    {
        if (!drivers.IsRegistered(connection.ProviderKind) || contexts.BindingFor(connection) is not { } binding)
        {
            return ProviderActionResolution.Refused(
                new ProviderActionDispatchOutcome(
                    ProviderDispatchRefusal.UnknownFamily,
                    $"The connection '{connection.DisplayName}' is served by a provider family this build cannot "
                    + $"resolve, so the action '{actionId}' cannot be started.",
                    Invocation: null,
                    Result: null));
        }

        var driver = drivers.GetRequired(connection.ProviderKind);
        var declaration = driver.Declaration;

        // The key travels as a request field because it carries a separator, and it is checked rather than
        // trusted: a caller naming a different family from the one serving the connection is refused instead of
        // quietly getting the connection's own family.
        if (!string.IsNullOrWhiteSpace(addInKey)
            && !string.Equals(addInKey, declaration.Key, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderActionResolution.Refused(
                new ProviderActionDispatchOutcome(
                    ProviderDispatchRefusal.UnknownFamily,
                    $"The connection '{connection.DisplayName}' is served by the provider family "
                    + $"'{declaration.Key}' and not by '{addInKey}'.",
                    Invocation: null,
                    Result: null));
        }

        var action = declaration.Actions
            .FirstOrDefault(declared => string.Equals(declared.Id, actionId, StringComparison.Ordinal));

        if (action is null)
        {
            var declared = declaration.Actions.Count == 0
                ? "it declares none"
                : $"it declares {string.Join(", ", declaration.Actions.Select(each => $"'{each.Id}'"))}";

            return ProviderActionResolution.Refused(
                new ProviderActionDispatchOutcome(
                    ProviderDispatchRefusal.UnknownAction,
                    $"The provider family '{declaration.Key}' declares no action '{actionId}'; {declared}.",
                    Invocation: null,
                    Result: null));
        }

        // Checked here rather than after a run is opened: whether a family can run its own declared action is a
        // property of the family and not of the run, so it is a refusal like any other and leaves no record of a
        // run that never started.
        if (driver is not IAiProviderActions)
        {
            return ProviderActionResolution.Refused(
                new ProviderActionDispatchOutcome(
                    ProviderDispatchRefusal.UnknownAction,
                    $"The provider family '{declaration.Key}' declares the action '{action.Id}' but supplies nothing "
                    + "to run it.",
                    Invocation: null,
                    Result: null));
        }

        return ProviderActionResolution.Of((driver, declaration, action, binding));
    }

    private async Task<ProviderActionDispatchOutcome> RunAsync(
        AiConnectionDto connection,
        ProviderAddInBinding binding,
        IAiProviderDriver driver,
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        ProviderActionInvocation invocation,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken ct)
    {
        // The run is already open, so everything between here and the family's own call closes it on failure.
        // Naming who initiated the run reads the database, and building the primitives reads the credential; a
        // failure in either would otherwise escape with the invocation left Pending and no cause recorded on it
        // until its window lapses.
        IReadOnlyCollection<string> submittedSecrets;
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets;
        IProviderActionContext? context;
        try
        {
            var ownerDisplayName = await ownerRoles
                .DescribeAdministratorAsync(invocation.InitiatingAdminId, ct)
                .ConfigureAwait(false);

            submittedSecrets = SecretInputsOf(action, inputs);
            secrets = Combined(contexts.SecretsFor(binding), submittedSecrets);
            context = contexts.ForInvocation(connection, invocation, ownerDisplayName, submittedSecrets);
        }
        catch (OperationCanceledException)
        {
            // A caller that stops waiting during the setup ends the setup with it, and the run was opened before
            // any of it started. Closed here rather than reported, because nothing was started for the operator
            // to come back to and the alternative is a row advertising a run until its window lapses.
            LogActionAbandonedDuringSetup(logger, declaration.Key, action.Id, invocation.Id);

            return await this.CloseAsync(
                    declaration,
                    action,
                    invocation,
                    ProviderActionResult.Failed(
                        $"The '{action.Label}' action of the '{declaration.Label}' provider family did not start "
                        + "because the request that opened it was abandoned."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogActionSetupThrew(logger, exception, declaration.Key, action.Id, invocation.Id);

            // The exception is a host failure rather than a family's message, so it is logged and not repeated
            // to the operator: a connection string or a credential the data layer quoted would travel with it.
            return await this.CloseAsync(
                    declaration,
                    action,
                    invocation,
                    ProviderActionResult.Failed(
                        $"The '{action.Label}' action of the '{declaration.Label}' provider family could not be "
                        + "started because the host failed to prepare it. The cause is in the host's log."))
                .ConfigureAwait(false);
        }

        if (context is null)
        {
            return await this.CloseAsync(
                    declaration,
                    action,
                    invocation,
                    ProviderActionResult.Failed(
                        $"The connection '{connection.DisplayName}' has no resolvable provider family, so its "
                        + "actions cannot run."))
                .ConfigureAwait(false);
        }

        // Established while the request was resolved, before a run was opened.
        var runnable = (IAiProviderActions)driver;

        // Composed after the run was opened, so a stored row the endpoint rules no longer accept would otherwise
        // leave the run open until its window closed with nothing recorded against it. ProviderEndpoint refuses a
        // base URL at construction, and a row saved before a rule tightened reaches this line.
        ProviderEndpoint endpoint;
        try
        {
            endpoint = connection.ToProviderEndpoint(context);
        }
        catch (Exception failure) when (failure is ArgumentException or UriFormatException or InvalidOperationException)
        {
            LogActionSetupFailed(logger, declaration.Key, action.Id, failure);

            return await this.CloseAsync(
                    declaration,
                    action,
                    invocation,
                    ProviderActionResult.Failed(
                        $"The connection '{connection.DisplayName}' holds settings the '{declaration.Label}' "
                        + "provider family no longer accepts, so its actions cannot run. The cause is in the "
                        + "host's log."))
                .ConfigureAwait(false);
        }

        var work = this.InvokeAsync(runnable, endpoint, declaration, action, invocation.Id, inputs, context, secrets);

        if (!await this.AnsweredWithinWaitAsync(work, invocation, ct).ConfigureAwait(false))
        {
            LogActionStillRunning(logger, declaration.Key, action.Id, invocation.Id);
            this.RecordWhenItFinishes(declaration, action, invocation, work);

            return new ProviderActionDispatchOutcome(
                ProviderDispatchRefusal.None,
                Message: null,
                invocation,
                Result: null);
        }

        return await this.CloseAsync(declaration, action, invocation, await work.ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>Runs the family's action and turns whatever it throws into a failure the host can record.</summary>
    /// <remarks>
    ///     Nothing a family does leaves this as an exception. Both the call that started the action and the path
    ///     that records a late answer read the returned result, and an exception escaping either would leave a
    ///     run pending until its window closed with no reason recorded against it.
    /// </remarks>
    private async Task<ProviderActionResult> InvokeAsync(
        IAiProviderActions runnable,
        ProviderEndpoint endpoint,
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        Guid invocationId,
        IReadOnlyDictionary<string, string> inputs,
        IProviderActionContext context,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets)
    {
        try
        {
            var result = await runnable.InvokeAsync(endpoint, action.Id, inputs, context).ConfigureAwait(false);

            return result is null
                ? ProviderActionResult.Failed(
                    $"The provider family '{declaration.Label}' answered the action '{action.Label}' with "
                    + "nothing.")
                : await CheckedAsync(declaration, endpoint, result, secrets).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Deliberately every exception. This is the boundary with code the host does not own, and a family
            // that fails has to leave a reason on the run rather than an unobserved task. The message is one a
            // family wrote, so it is capped and scrubbed like every other string it produces: a refused vendor
            // call is one of the places a credential comes back quoted.
            LogActionThrew(logger, exception, declaration.Key, action.Id, invocationId);

            return ProviderActionResult.Failed(
                $"The '{action.Label}' action of the '{declaration.Label}' provider family failed: "
                + await ProviderReportedMessage.ScrubbedAsync(exception.Message, secrets).ConfigureAwait(false));
        }
    }

    /// <summary>
    ///     Waits for the family's answer for as long as the host waits, and reports whether one arrived.
    /// </summary>
    /// <remarks>
    ///     The wait is the shorter of the host's dispatch wait and what is left of the run's window, and the
    ///     caller's own cancellation ends it too. None of the three cancels the family's work, which is the
    ///     point: the run stays open and the operator's view reads it.
    /// </remarks>
    private async Task<bool> AnsweredWithinWaitAsync(
        Task<ProviderActionResult> work,
        ProviderActionInvocation invocation,
        CancellationToken ct)
    {
        var remaining = invocation.ExpiresAt - timeProvider.GetUtcNow();
        var wait = remaining < this.DispatchWait ? remaining : this.DispatchWait;

        if (wait <= TimeSpan.Zero)
        {
            return work.IsCompleted;
        }

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var finished = await Task.WhenAny(work, Task.Delay(wait, timeProvider, waiting.Token))
            .ConfigureAwait(false);

        // Releases the timer whichever side won, so a host serving many actions does not accumulate one per
        // answer that arrived early.
        await waiting.CancelAsync().ConfigureAwait(false);

        return ReferenceEquals(finished, work);
    }

    /// <summary>
    ///     Records the family's answer against the run when it arrives after the call that started it returned.
    /// </summary>
    /// <remarks>
    ///     Not awaited, because the call it belongs to is over. What it buys is that a family answering a little
    ///     late still closes its own run rather than leaving it to expire with an answer nobody read. A run that
    ///     has already reached a terminal state is unaffected, because closing one is write-once.
    /// </remarks>
    private void RecordWhenItFinishes(
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        ProviderActionInvocation invocation,
        Task<ProviderActionResult> work)
    {
        _ = work.ContinueWith(
            async finished =>
            {
                try
                {
                    await this.CloseAsync(declaration, action, invocation, finished.Result).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogActionThrew(logger, exception, declaration.Key, action.Id, invocation.Id);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>Applies one result to the run and reports where the run stands afterwards.</summary>
    /// <remarks>
    ///     Takes no cancellation token. Recording the terminal state is what every path through this class exists
    ///     to reach, and one of those paths is a caller whose token is already cancelled: passing that token to
    ///     the write would leave the run Pending in exactly the case the write was added for.
    /// </remarks>
    /// <param name="declaration">The family whose action it is.</param>
    /// <param name="action">The action, as the family declared it.</param>
    /// <param name="invocation">The run the result belongs to.</param>
    /// <param name="result">What the family answered, or what the host recorded in its place.</param>
    private async Task<ProviderActionDispatchOutcome> CloseAsync(
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        ProviderActionInvocation invocation,
        ProviderActionResult result)
    {
        var terminal = TerminalStateOf(result);

        // A run whose window has already closed keeps the expiry rather than taking an answer that arrived after
        // the host gave up on it, so the reading an operator was left with is the one that stands.
        if (terminal is not null && timeProvider.GetUtcNow() < invocation.ExpiresAt)
        {
            await invocations
                .TryCloseAsync(invocation.Id, terminal, MessageOf(result, declaration), CancellationToken.None)
                .ConfigureAwait(false);
            cancellations.Close(invocation.Id);
        }

        var current = await invocations.GetAsync(invocation.Id, CancellationToken.None).ConfigureAwait(false)
                      ?? invocation;
        LogActionEnded(logger, declaration.Key, action.Id, invocation.Id, current.State);

        return new ProviderActionDispatchOutcome(
            ProviderDispatchRefusal.None,
            Message: null,
            current,
            result);
    }

    /// <summary>Either what the dispatch resolved to, or the refusal that stopped it.</summary>
    private readonly record struct ProviderActionResolution(
        (IAiProviderDriver Driver,
            ProviderDeclaration Declaration,
            ProviderDeclaredAction Action,
            ProviderAddInBinding Binding)? Resolved,
        ProviderActionDispatchOutcome? Refusal)
    {
        public static ProviderActionResolution Of(
            (IAiProviderDriver Driver,
                ProviderDeclaration Declaration,
                ProviderDeclaredAction Action,
                ProviderAddInBinding Binding) resolved)
        {
            return new ProviderActionResolution(resolved, Refusal: null);
        }

        public static ProviderActionResolution Refused(ProviderActionDispatchOutcome refusal)
        {
            return new ProviderActionResolution(Resolved: null, refusal);
        }
    }
}
