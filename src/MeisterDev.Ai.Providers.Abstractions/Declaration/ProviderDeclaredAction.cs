// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     An operation an operator can start against one connection: a credential flow, a disconnect, a re-check.
/// </summary>
/// <remarks>
///     A family declares operations rather than serving routes. The host exposes one dispatch path, authorizes
///     it against the connection's owner, bounds it, and honours the closed set of results without knowing why
///     any of them was returned.
/// </remarks>
/// <param name="Id">Stable identifier the dispatch request names; unique within the family.</param>
/// <param name="Label">What an operator sees on the affordance that starts it.</param>
/// <param name="Inputs">
///     The values the action needs from the operator. These are action inputs and never persist, so a value that
///     arrives this way — a pasted callback URL carrying a live authorization code, for instance — reaches no
///     column.
/// </param>
/// <exception cref="ArgumentException">
///     The id or the label is blank, or an input is declared with connection scope.
/// </exception>
public sealed record ProviderDeclaredAction(
    string Id,
    string Label,
    IReadOnlyList<ProviderDeclaredField> Inputs)
{
    // Held in fields so that setting either goes through an accessor that checks it: an object initializer and a
    // `with` expression both write over what the constructor set, and a property with an accessor body cannot
    // carry a field initializer of its own. Every positional member is declared here, in the order of the
    // parameters, because a record that declares only some of them emits the rest first.
    private readonly string _id = ActionId(Id, nameof(Id));
    private readonly string _label = Named(Label, nameof(Label));
    private readonly IReadOnlyList<ProviderDeclaredField> _inputs = ActionInputs(Inputs, nameof(Inputs));

    /// <summary>The longest identifier an action may be declared with.</summary>
    /// <remarks>
    ///     The host records the identifier on every invocation of the action, in a column of this width. Refused
    ///     here rather than at the column, because an id the host cannot record declares an action a console
    ///     offers and the first dispatch refuses.
    /// </remarks>
    public const int MaximumIdLength = 200;

    /// <summary>
    ///     Stable identifier the dispatch request names. A blank id is refused wherever it is set: the dispatch
    ///     path selects an action by this value, so a blank one declares an action nothing can start.
    /// </summary>
    public string Id
    {
        get => this._id;
        init => this._id = ActionId(value, nameof(ProviderDeclaredAction.Id));
    }

    /// <summary>
    ///     What an operator sees on the affordance that starts the action. Blank is refused because the console
    ///     would render an unlabelled control.
    /// </summary>
    public string Label
    {
        get => this._label;
        init => this._label = Named(value, nameof(ProviderDeclaredAction.Label));
    }

    /// <summary>
    ///     The values the action needs from the operator, every one of them scoped to the invocation. A
    ///     connection-scoped field is refused here: the host persists what carries that scope, and this type
    ///     documents its values as reaching no column.
    /// </summary>
    public IReadOnlyList<ProviderDeclaredField> Inputs
    {
        get => this._inputs;
        init => this._inputs = ActionInputs(value, nameof(ProviderDeclaredAction.Inputs));
    }

    /// <summary>
    ///     Whether this action finishes in a browser on the machine running the host, or null to follow
    ///     <see cref="ProviderDeclaration.RequiresBrowserCoLocation" />.
    /// </summary>
    /// <remarks>
    ///     Stated per action because a family with a credential flow has actions of both kinds: the sign-in
    ///     completes in a browser, and a disconnect that clears the stored credential completes in the host. The
    ///     requirement is what an operator is told before the action starts, and telling them to start a
    ///     disconnect from a particular machine states something that is not so. Null on an action that does not
    ///     set it, which leaves every family that states nothing here reading as it did.
    /// </remarks>
    public bool? RequiresBrowserCoLocation { get; init; }

    private static string ActionId(string value, string parameter)
    {
        var named = Named(value, parameter);

        return named.Length <= ProviderDeclaredAction.MaximumIdLength
            ? named
            : throw new ArgumentException(
                $"The action id is {named.Length} characters; the host records at most "
                + $"{ProviderDeclaredAction.MaximumIdLength}.",
                parameter);
    }

    private static string Named(string value, string parameter)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                "A declared action needs an id and a label. The id is what a dispatch request names it by, and "
                + "the label is what an operator sees on the affordance that starts it.",
                parameter)
            : value;
    }

    private static IReadOnlyList<ProviderDeclaredField> ActionInputs(
        IReadOnlyList<ProviderDeclaredField> inputs,
        string parameter)
    {
        ArgumentNullException.ThrowIfNull(inputs, parameter);

        // Copied first, then checked, and the copy is what is kept. The parameter is an interface a caller can
        // satisfy with a mutable list, so validating one enumeration and copying another checks a set of inputs
        // that is not necessarily the set that ends up stored. The copy is also what keeps a declaration — a
        // process-wide instance every add-in reaches — from being written through a cast back to the list.
        var declared = inputs.ToImmutableArray();

        var persisted = declared
            .Where(input => input is null || input.Scope != ProviderFieldScope.ActionInput)
            .Select(input => input is null ? "(none)" : $"'{input.Name}'")
            .ToList();

        if (persisted.Count > 0)
        {
            throw new ArgumentException(
                $"The action input(s) {string.Join(", ", persisted)} are not declared with "
                + $"'{nameof(ProviderFieldScope.ActionInput)}' scope. An action's values are collected for one "
                + "invocation and never stored, and a connection-scoped field reaches the column a connection is "
                + "saved into.",
                parameter);
        }

        return declared;
    }
}
