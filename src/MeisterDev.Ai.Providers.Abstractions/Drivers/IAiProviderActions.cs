// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Runs the operations a family declared, when an operator starts one against a connection.
/// </summary>
/// <remarks>
///     <para>
///         Separate from <see cref="IAiProviderDriver" /> rather than a member of it. The contract carries no
///         compatibility guarantee, so adding a member to the driver interface stops every family built against
///         the previous contract from loading, while a second interface a family may implement leaves them
///         loading unchanged. A family that declares no action implements nothing.
///     </para>
///     <para>
///         A family serves no route and sees no request. The host resolves the connection, derives the role its
///         owner requires, refuses before this is called, opens a bounded invocation, and hands over a context
///         already bound to that connection. What the action does with it is the family's own business, and the
///         host honours the result without knowing why it was returned.
///     </para>
/// </remarks>
public interface IAiProviderActions
{
    /// <summary>Runs one declared action against one connection.</summary>
    /// <remarks>
    ///     <para>
    ///         The call may return long before the work finishes. An operator signing in at a vendor takes
    ///         minutes, and the completion arrives on a socket the family opened rather than on the call that
    ///         started it, so a family whose work outlives the dispatch returns
    ///         <see cref="ProviderActionResult.OpenUrl" /> or <see cref="ProviderActionResult.ShowForm" /> and
    ///         writes its terminal state through <see cref="IProviderActionContext.Invocation" /> when the work
    ///         is over.
    ///     </para>
    ///     <para>
    ///         A call that has to wait observes <see cref="IProviderActionContext.Cancellation" />, which the
    ///         host trips at the invocation's window and at shutdown. A family that blocks a thread without
    ///         observing it is not cut; the invocation expires either way and neither the operator's view nor the
    ///         call that started the action is held, which leaves the blocked thread with the family that blocked
    ///         it.
    ///     </para>
    /// </remarks>
    /// <param name="endpoint">
    ///     The connection as this family configured it, carrying its declared values. Its
    ///     <see cref="ProviderEndpoint.HostContext" /> is the same context as <paramref name="context" />.
    /// </param>
    /// <param name="actionId">The action, as this family declared it.</param>
    /// <param name="inputs">
    ///     The values the operator supplied for this invocation, by declared input name. Empty on the call that
    ///     starts an action, and carrying a submitted form's values on a call that continues one.
    /// </param>
    /// <param name="context">What the host allows this family against this connection and this invocation.</param>
    Task<ProviderActionResult> InvokeAsync(
        ProviderEndpoint endpoint,
        string actionId,
        IReadOnlyDictionary<string, string> inputs,
        IProviderActionContext context);

    /// <summary>
    ///     The ids of the declared actions one connection is offered, from what the host knows about it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A declaration states which operations exist, and this states which of them apply to one connection:
    ///         a family whose credential is written by its own sign-in offers the sign-in before that has happened
    ///         and the disconnect after, and both are the same declaration.
    ///     </para>
    ///     <para>
    ///         Synchronous and free of I/O. It is called once per connection while a list is projected, so it must
    ///         not reach the network, the database or the credential store; everything it needs to decide with is
    ///         on <paramref name="connection" />.
    ///     </para>
    ///     <para>
    ///         An id returned here that the family never declared is ignored by the host. The console renders from
    ///         the declaration, so an undeclared id has no label, no inputs and nothing to dispatch against.
    ///     </para>
    /// </remarks>
    /// <param name="connection">What the host knows about the connection being projected.</param>
    /// <returns>
    ///     The subset of declared action ids to offer. An empty set offers none. The default offers every id the
    ///     family declared, so a family that states nothing here is unaffected by this member.
    /// </returns>
    IReadOnlyList<string> OfferedActionIds(ProviderConnectionState connection)
    {
        // Read from the declaration on this same object. A family implements this interface on its driver — the
        // host resolves the driver and then casts it to this interface to run an action — so there is one to
        // read from wherever the host asks.
        return this is IAiProviderDriver driver
            ? [.. driver.Declaration.Actions.Select(action => action.Id)]
            : [];
    }
}
