// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>Whether a declared value belongs to the connection or to one action invocation.</summary>
public enum ProviderFieldScope
{
    /// <summary>Connection configuration. Persisted with the connection and read back on every use.</summary>
    Connection = 0,

    /// <summary>
    ///     An input to one action invocation. Never persisted, because the values that arrive this way are things
    ///     like a pasted callback URL carrying a live authorization code.
    /// </summary>
    ActionInput = 1,
}

/// <summary>
///     Shows a field only when another field holds a stated value, which is the one conditional a form needs
///     before a family arrives that needs more.
/// </summary>
/// <remarks>
///     A hidden field is neither submitted nor validated, so an operator cannot be wedged by a message keyed to a
///     field the form does not show.
/// </remarks>
/// <param name="FieldName">The field whose value decides.</param>
/// <param name="EqualsValue">The value that makes this field visible, rendered as the deciding field renders it.</param>
public sealed record ProviderFieldVisibility(string FieldName, string EqualsValue);

/// <summary>
///     One value a family asks an operator for, described so a host can render, validate, store and read it back
///     without knowing what it is for.
/// </summary>
/// <param name="Name">The key the value is stored and read back under.</param>
/// <param name="Label">What an operator sees where the value is entered.</param>
/// <param name="Kind">The value shape, from the closed vocabulary.</param>
/// <exception cref="ArgumentException">The name or the label is blank.</exception>
public sealed record ProviderDeclaredField(string Name, string Label, ProviderFieldKind Kind)
{
    // Held in fields so a value is checked or copied wherever it is set: an object initializer and a `with`
    // expression both write over what the constructor set, and a property with an accessor body cannot carry a
    // field initializer of its own.
    private readonly IReadOnlyList<string> _choices = ImmutableArray<string>.Empty;
    private readonly string _name = Named(Name, nameof(Name));
    private readonly string _label = Named(Label, nameof(Label));

    /// <summary>
    ///     The key the value is stored and read back under. Blank is refused: the name is a key in the settings
    ///     document, in the credential envelope and in the submitted map, and a blank one names a value the
    ///     family writes and never reads back.
    /// </summary>
    public string Name
    {
        get => this._name;
        init => this._name = Named(value, nameof(ProviderDeclaredField.Name));
    }

    /// <summary>
    ///     What an operator sees where the value is entered. Blank is refused, because the console renders an
    ///     unlabelled input and a refusal about the field names nothing.
    /// </summary>
    public string Label
    {
        get => this._label;
        init => this._label = Named(value, nameof(ProviderDeclaredField.Label));
    }

    /// <summary>
    ///     Whether the connection can be saved without it. Optional unless stated, which is the opposite of a
    ///     credential field's default: a credential a family declared is one it needs, while most of what an
    ///     operator can configure has a working default.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>Guidance on what to enter, for a field whose label is not enough on its own.</summary>
    public string? Hint { get; init; }

    /// <summary>Example text shown in the empty input.</summary>
    public string? Placeholder { get; init; }

    /// <summary>
    ///     The value a new connection starts with. Where a vendor fixes a value — a registered redirect port, for
    ///     instance — this is where that value is stated, so an operator who moves it away from the vendor's
    ///     choice is making a deliberate change rather than filling in a blank.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>Whether the value is connection configuration or an input to one action invocation.</summary>
    public ProviderFieldScope Scope { get; init; } = ProviderFieldScope.Connection;

    /// <summary>
    ///     Whether the host computes this value and shows it read-only. A computed value is recomputed on every
    ///     render and never persisted, because it is composed from other fields and a stored copy goes stale the
    ///     moment one of them changes.
    /// </summary>
    public bool IsComputed { get; init; }

    /// <summary>The options a <see cref="ProviderFieldKind.Choice" /> field offers; empty for every other kind.</summary>
    /// <remarks>
    ///     Copied into an immutable list wherever it is set. A declaration is a process-wide instance every add-in
    ///     in the process can reach, and a read-only list over an array or a List is cast back to it in one line,
    ///     so holding what a caller passed would let one family's choices be rewritten from another.
    /// </remarks>
    public IReadOnlyList<string> Choices
    {
        get => this._choices;
        init => this._choices = value is null
            ? throw new ArgumentNullException(nameof(ProviderDeclaredField.Choices))
            : value.ToImmutableArray();
    }

    /// <summary>What has to hold for the field to be shown, or null for a field that is always shown.</summary>
    public ProviderFieldVisibility? VisibleWhen { get; init; }

    /// <summary>
    ///     Whether an operator may paste into this input the whole address the provider redirected to.
    /// </summary>
    /// <remarks>
    ///     Stated by the family rather than read off the field's shape. A redirect is collected as free text,
    ///     because the family parses the code out of it and the host never dials it, so the shape a host could
    ///     infer it from is the shape of every other free-text input. Left unset, the host falls back to reading
    ///     any free-text or address input of a co-located action as one, which it did before this
    ///     existed; a family that sets it on one input has the notice stated for that input alone.
    /// </remarks>
    public bool AcceptsRedirectAddress { get; init; }

    /// <summary>
    ///     Whether the value is credential material. Derived from <see cref="Kind" /> rather than declared beside
    ///     it, so the flag the host stores, renders and logs by cannot disagree with the shape the family
    ///     declared.
    /// </summary>
    public bool IsSecret => this.Kind == ProviderFieldKind.Secret;

    private static string Named(string value, string parameter)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                "A declared field needs a name and a label. The name is the key its value is stored and read "
                + "back under, and the label is what an operator sees where the value is entered.",
                parameter)
            : value;
    }
}
