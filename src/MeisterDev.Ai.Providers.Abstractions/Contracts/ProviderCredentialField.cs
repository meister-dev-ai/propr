// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     One named value a credential is made of, described so a host can collect, check and store it without
///     knowing what it is for.
/// </summary>
/// <remarks>
///     <para>
///         A credential is one string for most families, and a family is free to need several: an access key
///         paired with a secret and a session token, a client id beside a client secret. A host that assumed one
///         string could configure none of those, so the driver declares the fields each of its authentication
///         modes needs and the host collects exactly those.
///     </para>
///     <para>
///         The description is deliberately small — a name, a label, and the three facts a form needs. It is not a
///         form-description language: whether a field ends up in a header, is exchanged for a token, or is fed to
///         a signing algorithm is the driver's business, and a host that could describe that would be deciding it.
///     </para>
/// </remarks>
/// <param name="Name">Stable field name; see the <see cref="Name" /> property.</param>
/// <param name="Label">Human-readable name for the field; see the <see cref="Label" /> property.</param>
/// <param name="IsSecret">Whether the value is credential material; see the <see cref="IsSecret" /> property.</param>
/// <param name="IsRequired">Whether a profile can be saved without it.</param>
/// <param name="Hint">Optional guidance on what to enter, for a field whose label is not enough on its own.</param>
/// <exception cref="ArgumentException"><paramref name="Name" /> or <paramref name="Label" /> is blank.</exception>
public sealed record ProviderCredentialField(
    string Name,
    string Label,
    bool IsSecret = true,
    bool IsRequired = true,
    string? Hint = null)
{
    // The name and the label are held in fields so that setting either goes through an accessor that checks it.
    // A property initializer would run for the constructor alone, and an object initializer and a `with`
    // expression both write over what the constructor set. These field initializers are what carry the
    // constructor's arguments in, because a property with an accessor body cannot have one of its own.
    /// <summary>
    ///     The field name a credential that is one string is stored under. Declared here rather than with the
    ///     stored envelope, because the envelope is host-side and a driver has to name the field it reads back.
    /// </summary>
    public const string ApiKeyFieldName = "apiKey";

    private readonly string _name = Named(Name, nameof(Name));
    private readonly string _label = Named(Label, nameof(Label));

    // Every member is declared here so the name and the label can be checked on construction. The other three
    // are declared with them, and in the same order as the parameters, because a record that declares only some
    // of its positional members emits the rest first — which would reorder the serialized field and the
    // published schema without changing anything about the type.

    /// <summary>
    ///     Stable field name. It is the key the value is stored under in the credential envelope the host writes
    ///     and the key the driver reads it back by, so renaming one orphans the stored credentials. A blank name
    ///     is refused wherever it is set: it would store a value under a key nothing reads, and a refusal meant
    ///     to name the field would name nothing.
    /// </summary>
    public string Name
    {
        get => this._name;
        init => this._name = Named(value, nameof(ProviderCredentialField.Name));
    }

    /// <summary>
    ///     Human-readable name for the field, shown where an operator enters it. Blank is refused for the same
    ///     reason: a form would render an unlabelled box, and so would the refusals that name the field.
    /// </summary>
    public string Label
    {
        get => this._label;
        init => this._label = Named(value, nameof(ProviderCredentialField.Label));
    }

    /// <summary>
    ///     Whether the value is credential material. A console uses this to decide whether to mask the input; it
    ///     does not affect storage, because every field is stored inside the same protected envelope.
    /// </summary>
    public bool IsSecret { get; init; } = IsSecret;

    /// <summary>Whether a profile can be saved without it.</summary>
    public bool IsRequired { get; init; } = IsRequired;

    /// <summary>Optional guidance on what to enter, for a field whose label is not enough on its own.</summary>
    public string? Hint { get; init; } = Hint;

    private static string Named(string value, string parameter)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                "A credential field needs a name and a label. The name is the key its value is stored and read "
                + "back under, and the label is what an operator sees where the value is entered.",
                parameter)
            : value;
    }
}
