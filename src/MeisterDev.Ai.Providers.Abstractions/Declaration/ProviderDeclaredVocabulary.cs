// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     One authentication mode a family can authenticate with, and the named fields it writes for that mode.
/// </summary>
/// <remarks>
///     The field-name set is part of the declaration rather than something the host infers, because the host
///     collects those fields and stores them by name and the family reads them back by the same names. Stating
///     the set is what lets a conformance check confirm the family writes what it declared.
/// </remarks>
/// <param name="Mode">
///     The authentication mode, qualified by the declaring family's key: <c>meisterdev/anthropic:XApiKey</c>. The
///     credential axis has no host-reserved member, so every mode on it belongs to a family.
/// </param>
/// <param name="CredentialFields">
///     The fields that mode needs, in the order an operator is asked for them. Empty for a mode that needs no
///     stored credential, such as an ambient identity.
/// </param>
public sealed record ProviderDeclaredAuthMode(string Mode, IReadOnlyList<ProviderCredentialField> CredentialFields)
{
    // Held in a field so the copy is made wherever the list is set: a positional argument, an object initializer
    // and a `with` expression all reach the property, and a property with an accessor body cannot carry a field
    // initializer of its own. Every positional member is declared here, in the order of the parameters, because a
    // record that declares only some of them emits the rest first.
    private readonly string _mode = ValidMode(Mode);

    private readonly IReadOnlyList<ProviderCredentialField> _credentialFields =
        Held(CredentialFields, nameof(CredentialFields));

    /// <summary>The authentication mode, qualified by the declaring family's key.</summary>
    public string Mode
    {
        get => this._mode;
        init => this._mode = ValidMode(value);
    }

    /// <summary>
    ///     The fields that mode needs, in the order an operator is asked for them. Empty for a mode that needs
    ///     no stored credential, such as an ambient identity.
    /// </summary>
    public IReadOnlyList<ProviderCredentialField> CredentialFields
    {
        get => this._credentialFields;
        init => this._credentialFields = Held(value, nameof(ProviderDeclaredAuthMode.CredentialFields));
    }

    /// <summary>
    ///     What an operator sees where this mode is offered, or null to let the host read the mode name.
    /// </summary>
    /// <remarks>
    ///     Optional because the host can already derive a readable name from the mode name — <c>AzureIdentity</c>
    ///     reads as "Azure Identity" — and a family that states nothing is described that way. It is worth
    ///     stating where the derived name is wrong: <c>ApiKey</c> derives as "Api Key" and <c>SigV4</c> as
    ///     "Sig V4", neither of which is how the credential is spelled in the vendor's own documentation.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>Whether this mode is still read but no longer offered for a new connection.</summary>
    /// <remarks>
    ///     A mode stays declared after the family replaces it, because a profile saved under it holds its
    ///     credential in that mode and resolution, verification and the credential-field lookup all read the
    ///     declaration. Only the offer changes: a host builds its authentication-mode picker from the modes that
    ///     are not superseded, plus the one the profile being edited already holds, so that profile opens on the
    ///     mode it was saved under instead of on another one.
    /// </remarks>
    public bool Superseded { get; init; }

    /// <summary>The names this family writes into the stored credential for this mode.</summary>
    public IReadOnlyList<string> FieldNames => [.. this.CredentialFields.Select(declared => declared.Name)];

    // Present and not blank, and nothing further. A null reaches a comparison that dereferences it and a blank
    // names no mode, so both are refused where they are set. Whether the qualifier is this family's to declare
    // is a question this type cannot answer, because it does not hold the family's key; the vocabulary checks
    // answer it against the declaration and report it as a failure the author can read.
    private static string ValidMode(string mode)
    {
        return string.IsNullOrWhiteSpace(mode)
            ? throw new ArgumentException(
                "An authentication mode is the name a credential is stored and read back under, so a blank one "
                + "names nothing.",
                nameof(Mode))
            : mode;
    }

    // A declaration is a process-wide instance every add-in in the process can reach, and a read-only list over
    // an array or a List can be cast back to it, so holding what a caller passed would let one family's
    // declaration be rewritten from another.
    private static IReadOnlyList<T> Held<T>(IReadOnlyList<T> value, string parameter)
    {
        return value is null ? throw new ArgumentNullException(parameter) : value.ToImmutableArray();
    }
}

/// <summary>
///     The wire protocols a family speaks, with the host-reserved modes separated from the family's own.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Auto" /> and <see cref="Embeddings" /> are host-reserved and belong to no family. <c>Auto</c>
///         means "let the driver pick", which is host semantics and the default on every logical-model row, and
///         <c>Embeddings</c> is stored the same way. A family states which of those it honours and cannot declare
///         a new one; both persist unqualified.
///     </para>
///     <para>
///         Everything else is the family's own protocol mode, qualified by the family's key:
///         <c>meisterdev/anthropic:AnthropicMessages</c>. Two families that both speak a protocol each declare it,
///         because what a name means on the wire is the declaring family's to say, and the qualifier is what keeps
///         the two apart.
///     </para>
///     <para>
///         Held as one ordered list rather than two, so the order a family declares is the order a host offers,
///         and so the split into owned and reserved cannot disagree with what the family actually serves.
///     </para>
/// </remarks>
/// <param name="Supported">Every mode a binding may name against this family, in the family's own order.</param>
public sealed record ProviderDeclaredProtocolModes(IReadOnlyList<string> Supported)
{
    /// <summary>The host-reserved mode that leaves the wire format to the driver.</summary>
    public const string Auto = "Auto";

    /// <summary>The host-reserved shape for an embedding call.</summary>
    public const string Embeddings = "Embeddings";

    // Held in a field for the reason given on ProviderDeclaredAuthMode: the copy has to be made wherever the
    // list is set, and a property with an accessor body cannot carry a field initializer of its own.
    private readonly IReadOnlyList<string> _supported = DeclaredOnce(Supported, nameof(Supported));

    private readonly IReadOnlyDictionary<string, string> _labels =
        ImmutableDictionary<string, string>.Empty.WithComparers(ProviderVocabulary.ValueComparer);

    /// <summary>Every mode a binding may name against this family, in the family's own order.</summary>
    public IReadOnlyList<string> Supported
    {
        get => this._supported;
        init => this._supported = DeclaredOnce(value, nameof(ProviderDeclaredProtocolModes.Supported));
    }

    /// <summary>
    ///     What an operator sees where a shape is offered, for the shapes this family names differently from the
    ///     host's reading of the mode name. A shape absent from this map is described from its mode name.
    /// </summary>
    /// <remarks>
    ///     Optional for the reason <see cref="ProviderDeclaredAuthMode.Label" /> is: <c>AnthropicMessages</c>
    ///     already reads as "Anthropic Messages", and only the shapes whose derived name is wrong need stating.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Labels
    {
        get => this._labels;
        init => this._labels = LabelledOnce(value);
    }

    /// <summary>The host-reserved modes, which no family owns and none may add to.</summary>
    /// <remarks>
    ///     Immutable because every family's declaration is split against this one instance, and a caller that
    ///     could cast it back to the array would change which modes count as owned for all of them.
    /// </remarks>
    public static IReadOnlyList<string> Reserved { get; } = ImmutableArray.Create(Auto, Embeddings);

    /// <summary>Whether <paramref name="mode" /> is one of the host-reserved modes.</summary>
    /// <param name="mode">The mode to check, or null.</param>
    public static bool IsReserved(string? mode)
    {
        return ProviderVocabulary.Names(Reserved, mode);
    }

    /// <summary>The host-reserved modes this family serves.</summary>
    public IReadOnlyList<string> ReservedHonoured => [.. this.Supported.Where(IsReserved)];

    /// <summary>The protocol modes this family implements itself.</summary>
    public IReadOnlyList<string> Owned => [.. this.Supported.Where(mode => !IsReserved(mode))];

    // A mode is compared case-insensitively everywhere else, so two keys differing only in case are one mode
    // with two labels. ToImmutableDictionary throws ArgumentException on the collision with a message naming
    // neither the family nor the mode, at host startup, so the collision is named here instead.
    private static IReadOnlyDictionary<string, string> LabelledOnce(IReadOnlyDictionary<string, string> value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(ProviderDeclaredProtocolModes.Labels));
        }

        var labelled = new Dictionary<string, string>(ProviderVocabulary.ValueComparer);
        foreach (var (mode, label) in value)
        {
            if (!labelled.TryAdd(mode, label))
            {
                throw new ArgumentException(
                    $"The protocol mode '{mode}' is labelled more than once. A mode is compared without regard to "
                    + "case, so two keys differing only in case name one mode.",
                    nameof(ProviderDeclaredProtocolModes.Labels));
            }
        }

        return labelled.ToImmutableDictionary(ProviderVocabulary.ValueComparer);
    }

    // Every entry is a host-reserved mode or a value qualified by the declaring family's key, and no entry is
    // repeated. A null reaches a comparison that dereferences it, and a repeat offers the same mode twice in
    // every picker the declaration feeds while a binding holding it is one stored value.
    private static IReadOnlyList<string> DeclaredOnce(IReadOnlyList<string> value, string parameter)
    {
        var held = Held(value, parameter);
        var seen = new HashSet<string>(ProviderVocabulary.ValueComparer);

        foreach (var mode in held)
        {
            if (mode is null)
            {
                throw new ArgumentException("A declared protocol mode is a value, and one of these is null.", parameter);
            }

            if (!IsReserved(mode) && ProviderVocabulary.ValidateQualifiedValue(mode) is { } refusal)
            {
                throw new ArgumentException(refusal.Message, parameter);
            }

            if (!seen.Add(mode))
            {
                throw new ArgumentException(
                    $"The protocol mode '{mode}' is declared more than once. Two entries naming one mode offer it "
                    + "twice while a binding holding it is one stored value.",
                    parameter);
            }
        }

        return held;
    }

    private static IReadOnlyList<T> Held<T>(IReadOnlyList<T> value, string parameter)
    {
        return value is null ? throw new ArgumentNullException(parameter) : value.ToImmutableArray();
    }
}
