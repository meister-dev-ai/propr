// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Globalization;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     Everything a host needs to know about a provider family before it reaches the family's driver: what the
///     family is called, what it needs configured, how it authenticates, what it speaks, what an operator can
///     start against it, where it goes, and what it needs licensed.
/// </summary>
/// <remarks>
///     <para>
///         The declaration is supplied by the family and read by the host. It names no web framework, no
///         object-relational mapper and no vendor SDK, so a family built outside this repository can construct
///         one against the contract assembly alone.
///     </para>
///     <para>
///         <strong>It is complete as declared.</strong> The contract carries no compatibility guarantee, so a
///         member added later is a recompile of every family built against it, including families this team does
///         not hold. A member nothing reads yet is still declared, because the alternative is that every rule
///         needing it waits for the next coordinated rebuild.
///     </para>
///     <para>
///         The credential and wire-protocol axes are typed as the host's own enumerations while those axes are
///         still closed. They become names a family declares, qualified by its key, when the vocabulary opens;
///         <see cref="LegacyNames" /> already carries the spelling each mode supersedes so the rows written
///         today stay readable through that change.
///     </para>
/// </remarks>
public sealed record ProviderDeclaration
{
    /// <summary>The longest an identity key may be, matching the narrowest column that stores one.</summary>
    public const int MaximumKeyLength = ProviderVocabulary.MaximumKeyLength;

    private readonly string _key = string.Empty;
    private readonly IReadOnlyList<ProviderDeclaredAuthMode> _authModes = ImmutableArray<ProviderDeclaredAuthMode>.Empty;
    private readonly IReadOnlyList<ProviderDeclaredField> _fields = ImmutableArray<ProviderDeclaredField>.Empty;
    private readonly IReadOnlyList<ProviderDeclaredAction> _actions = ImmutableArray<ProviderDeclaredAction>.Empty;
    private readonly IReadOnlyList<string> _reachedHostPatterns = ImmutableArray<string>.Empty;
    private readonly IReadOnlyList<string> _listenerPortFieldNames = ImmutableArray<string>.Empty;

    /// <summary>
    ///     The family's identity: a vendor prefix, a single <c>/</c>, and an internal name, for example
    ///     <c>meisterdev/openAi</c>.
    /// </summary>
    /// <remarks>
    ///     Held as a declared constant rather than derived from the assembly name, so renaming an assembly is not
    ///     a data migration. This is what persists against every connection, and it stays stable for the life of
    ///     the family. ASCII letters, digits and hyphen, one separator, at most
    ///     <see cref="MaximumKeyLength" /> characters including it; stored as declared and compared ignoring
    ///     case, so two spellings that differ only in case are one family rather than two.
    /// </remarks>
    /// <exception cref="ArgumentException">The key is not a well-formed identity key.</exception>
    public required string Key
    {
        get => this._key;
        init => this._key = ValidKey(value);
    }

    /// <summary>The human-readable name shown beside the key. Never persisted as identity, because two families may share one.</summary>
    public required string Label { get; init; }

    /// <summary>The family's own version, shown in the inventory so an operator can tell which build is loaded.</summary>
    public required string Version { get; init; }

    /// <summary>
    ///     The contract version this family was built against, checked when it is loaded. A family built against
    ///     an older contract is refused at load rather than failing on its first model call.
    /// </summary>
    public required string ContractVersion { get; init; }

    /// <summary>The stored spellings this family supersedes, honoured on read until its rows are rewritten.</summary>
    public ProviderLegacyNames LegacyNames { get; init; } = ProviderLegacyNames.None;

    /// <summary>
    ///     The values an operator supplies, each stated well enough for a host to render, validate, store and
    ///     read it back. Connection configuration and action inputs are both declared here and told apart by
    ///     their scope.
    /// </summary>
    public IReadOnlyList<ProviderDeclaredField> Fields
    {
        get => this._fields;
        init => this._fields = Held(value, nameof(ProviderDeclaration.Fields));
    }

    /// <summary>What this family knows about the shape of a request its endpoint accepts.</summary>
    public ProviderRequestShapeDefaults RequestShapeDefaults { get; init; } = ProviderRequestShapeDefaults.Unstated;

    /// <summary>
    ///     What to tell an operator about the connection values the host collects for every family — the display
    ///     name, the base URL and the default query parameters — or null for a family that states nothing about
    ///     them and leaves a console's family-neutral text in place.
    /// </summary>
    public ProviderConnectionForm? ConnectionForm { get; init; }

    /// <summary>The operations an operator can start against one connection of this family.</summary>
    public IReadOnlyList<ProviderDeclaredAction> Actions
    {
        get => this._actions;
        init => this._actions = Held(value, nameof(ProviderDeclaration.Actions));
    }

    /// <summary>The authentication modes this family authenticates with, and the fields it writes for each.</summary>
    /// <remarks>
    ///     One entry per shape. A second entry for a shape already declared states two sets of fields for one
    ///     credential, and the host has no basis for choosing between them; refused here rather than left to
    ///     throw out of <see cref="CredentialFields" /> while a registry is being built, where the failure names
    ///     neither the family nor the declaration.
    /// </remarks>
    /// <exception cref="ArgumentException">Two entries declare the same authentication mode.</exception>
    public required IReadOnlyList<ProviderDeclaredAuthMode> AuthModes
    {
        get => this._authModes;
        init => this._authModes = this.OneEntryPerMode(value);
    }

    /// <summary>The wire protocols this family speaks, and which host-reserved members it honours.</summary>
    public required ProviderDeclaredProtocolModes ProtocolModes { get; init; }

    /// <summary>
    ///     Whether this family needs the host process and the operator's browser on one machine. Stated rather
    ///     than enforced: the host cannot observe where a browser runs, so what this drives is what an operator is
    ///     told before starting an action, and the diagnosis on an invocation that expires waiting.
    /// </summary>
    public bool RequiresBrowserCoLocation { get; init; }

    /// <summary>
    ///     The hosts this family will contact. An entry is a bare host such as <c>api.openai.com</c>, or a
    ///     leading-dot suffix such as <c>.openai.azure.com</c> standing for that host and every subdomain of it.
    /// </summary>
    /// <remarks>
    ///     The suffix form is required rather than convenient: an endpoint whose hostname carries the operator's
    ///     own resource name cannot be enumerated by the family that reaches it. A tenant's endpoint restriction
    ///     is checked against these patterns, and every one of them has to be permitted, so a family cannot carry
    ///     an undeclared host in on the back of a declared one.
    /// </remarks>
    public IReadOnlyList<string> ReachedHostPatterns
    {
        get => this._reachedHostPatterns;
        init => this._reachedHostPatterns = Held(value, nameof(ProviderDeclaration.ReachedHostPatterns));
    }

    /// <summary>
    ///     The premium capability an installation needs before this family may be configured or called, or null
    ///     for a family that needs none. An opaque string: the family carries no licensing code, and the host
    ///     checks the key against the licence.
    /// </summary>
    public string? RequiredCapabilityKey { get; init; }

    /// <summary>What the shared driver checks need from this family in order to run against it.</summary>
    public required ProviderConformanceInputs ConformanceInputs { get; init; }

    /// <summary>
    ///     How long an action of this family may stay open, or null for a family that declares no action.
    /// </summary>
    public ProviderInvocationWindow? InvocationWindow { get; init; }

    /// <summary>
    ///     Whether a connection of this family has a credential health state at all. A family whose credential is
    ///     a key an operator typed has nothing to report; a family holding a grant that can be revoked does.
    /// </summary>
    public bool HasCredentialHealth { get; init; }

    /// <summary>
    ///     Whether an action of this family opens a socket of its own. Compatibility metadata rather than a
    ///     control: the host neither opens the socket nor can prevent one being opened, and what this supports is
    ///     an operator reading the inventory and a diagnosis naming what an expired invocation was waiting for.
    /// </summary>
    public bool OpensListener { get; init; }

    /// <summary>
    ///     The configuration fields holding the port a browser is sent back to, for a family that opens a
    ///     listener; empty for one that names none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Named by the family rather than inferred by the host. Without it the host reads every whole-number
    ///         configuration field of a listener-opening family as a port, which is an assumption about what the
    ///         family's field shapes mean: a timeout in seconds declared beside a callback port is announced to
    ///         the operator as the port their browser will be sent back to.
    ///     </para>
    ///     <para>
    ///         Empty keeps that reading, so a family that says nothing behaves as it did. A name matching no
    ///         whole-number field is passed over, which shows one port fewer instead of the wrong one.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> ListenerPortFieldNames
    {
        get => this._listenerPortFieldNames;
        init => this._listenerPortFieldNames = FieldNames(value, nameof(ProviderDeclaration.ListenerPortFieldNames));
    }

    /// <summary>The authentication modes this family authenticates with.</summary>
    public IReadOnlyList<string> SupportedAuthModes => [.. this.AuthModes.Select(mode => mode.Mode)];

    /// <summary>The fields each declared authentication mode needs, keyed by mode.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
        this.AuthModes.ToDictionary(mode => mode.Mode, mode => mode.CredentialFields, ProviderVocabulary.ValueComparer);

    /// <summary>
    ///     The loopback ports this family listens on for one connection, read from the fields it named.
    /// </summary>
    /// <remarks>
    ///     Empty for a family that opens no listener, that named no port field, or whose named fields hold
    ///     nothing a port can be read from. A caller treats empty as "not known" and not as "none": the reading
    ///     depends on what the family declared about its own fields, and a family that declared nothing has told
    ///     the host nothing about which port it binds.
    /// </remarks>
    /// <param name="values">The connection's declared values, by field name.</param>
    public IReadOnlyList<int> ListenerPortsIn(IReadOnlyDictionary<string, string>? values)
    {
        if (!this.OpensListener || this.ListenerPortFieldNames.Count == 0)
        {
            return [];
        }

        var ports = new List<int>();
        foreach (var field in this.Fields)
        {
            if (field.Kind != ProviderFieldKind.Int
                || !this.ListenerPortFieldNames.Contains(field.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var configured = values?.GetValueOrDefault(field.Name);
            var stated = string.IsNullOrWhiteSpace(configured) ? field.DefaultValue : configured;

            if (int.TryParse(stated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535
                && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>The fields one authentication mode collects, or an empty list for a mode this family does not declare.</summary>
    /// <remarks>
    ///     Reads the authentication mode that declares them, so a caller needs no dictionary of its own and the
    ///     two cannot disagree. A mode the family does not declare is refused separately, by
    ///     <see cref="Drivers.AiAuthModeSupport" />, which names the mode and the family.
    /// </remarks>
    /// <param name="mode">The authentication mode being configured.</param>
    public IReadOnlyList<ProviderCredentialField> CredentialFieldsFor(string mode)
    {
        foreach (var declared in this.AuthModes)
        {
            if (ProviderVocabulary.ValuesEqual(declared.Mode, mode))
            {
                return declared.CredentialFields;
            }
        }

        return [];
    }

    /// <summary>The connection configuration this family asks for, without its action inputs.</summary>
    public IReadOnlyList<ProviderDeclaredField> ConnectionFields =>
        [.. this.Fields.Where(declared => declared.Scope == ProviderFieldScope.Connection)];

    /// <summary>Whether <paramref name="key" /> is a well-formed identity key.</summary>
    /// <param name="key">The key to check.</param>
    public static bool IsValidIdentityKey(string? key)
    {
        return ProviderVocabulary.IsValidIdentityKey(key);
    }

    /// <summary>Field names a declaration points at, each refused blank or padded.</summary>
    /// <remarks>
    ///     The host looks a name up against the declared fields by exact ordinal comparison, so a padded one
    ///     names no field and the reading silently falls back to the one the name was there to replace.
    /// </remarks>
    /// <param name="value">The names as the family declared them.</param>
    /// <param name="parameter">The member being set, for the refusal.</param>
    private static IReadOnlyList<string> FieldNames(IReadOnlyList<string> value, string parameter)
    {
        var held = Held(value, parameter);

        foreach (var name in held)
        {
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{name}' names no declared field. The host reads a configured value by this name exactly, "
                    + "so a blank or padded one matches nothing.",
                    parameter);
            }
        }

        return held;
    }

    /// <summary>
    ///     Copies a declared collection into an immutable one. A declaration is a process-wide instance every
    ///     add-in in the process can reach, and a read-only list over an array can be cast back to the array, so
    ///     holding what a caller passed would let one family's declaration be rewritten from another.
    /// </summary>
    /// <typeparam name="T">The declared item type.</typeparam>
    /// <param name="value">The collection as declared.</param>
    /// <param name="parameter">The member being set, named in the refusal.</param>
    private static IReadOnlyList<T> Held<T>(IReadOnlyList<T> value, string parameter)
    {
        return value is null
            ? throw new ArgumentNullException(parameter)
            : value.ToImmutableArray();
    }

    private IReadOnlyList<ProviderDeclaredAuthMode> OneEntryPerMode(IReadOnlyList<ProviderDeclaredAuthMode> authModes)
    {
        var held = Held(authModes, nameof(ProviderDeclaration.AuthModes));

        var repeated = held
            .GroupBy(declared => declared.Mode, ProviderVocabulary.ValueComparer)
            .Where(group => group.Count() > 1)
            .Select(group => $"'{group.Key}'")
            .ToList();

        if (repeated.Count > 0)
        {
            var declaration = string.IsNullOrEmpty(this._key) ? "A provider declaration" : $"The declaration '{this._key}'";
            throw new ArgumentException(
                $"{declaration} declares the authentication mode(s) {string.Join(", ", repeated)} more than once. "
                + "A mode names one set of fields, and a host offered two has no basis for choosing between them.",
                nameof(ProviderDeclaration.AuthModes));
        }

        return held;
    }

    private static string ValidKey(string key)
    {
        // Checked where it is set rather than where it is stored. The key is the persisted identity of every
        // connection of this family, so a malformed one is not a rendering problem but a row nothing can resolve,
        // and the separator rule is what lets a qualified vocabulary value be split back apart unambiguously.
        return ProviderVocabulary.ValidateKey(key) is { } refusal
            ? throw new ArgumentException(refusal.Message, nameof(key))
            : key;
    }
}
