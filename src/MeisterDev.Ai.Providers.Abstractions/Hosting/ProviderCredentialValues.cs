// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections;
using System.Collections.Frozen;
using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Credential values keyed by the field names an authentication mode declares.
/// </summary>
/// <remarks>
///     <para>
///         A plain <c>IReadOnlyDictionary&lt;string, string&gt;</c> carries the same shape as a header collection,
///         a query-parameter collection and an action's inputs, so a signature taking one says nothing about
///         which of them it wants. This type names the role, and holds the two rules a caller otherwise has to
///         know from a comment.
///     </para>
///     <para>
///         The first rule is the comparer. A declared field name is matched ordinally everywhere else, and a map
///         built with <see cref="StringComparer.OrdinalIgnoreCase" /> would answer a presence check one way and
///         an undeclared-name check the other. Construction copies into an ordinal map.
///     </para>
///     <para>
///         The second rule is that a blank value is not a value. A form posts every field it renders, so an empty
///         box arrives as an empty string; treating that as supplied would store a credential of no characters
///         and pass the required-field check. Construction drops them.
///     </para>
///     <para>
///         <c>ToString</c> renders the field names and no values, because these are the secrets themselves.
///     </para>
/// </remarks>
public sealed class ProviderCredentialValues : IReadOnlyDictionary<string, string>
{
    private readonly FrozenDictionary<string, string> _values;

    private ProviderCredentialValues(FrozenDictionary<string, string> values)
    {
        this._values = values;
    }

    /// <summary>No credential values, for a mode that stores none.</summary>
    public static ProviderCredentialValues None { get; } =
        new(FrozenDictionary<string, string>.Empty);

    /// <inheritdoc />
    public IEnumerable<string> Keys => this._values.Keys;

    /// <inheritdoc />
    public IEnumerable<string> Values => this._values.Values;

    /// <inheritdoc />
    public int Count => this._values.Count;

    /// <inheritdoc />
    public string this[string key] => this._values[key];

    /// <summary>
    ///     Takes a map of field name to value, dropping every entry whose value is blank.
    /// </summary>
    /// <param name="values">The values as they were supplied or read back, or null for none.</param>
    public static ProviderCredentialValues From(IReadOnlyDictionary<string, string>? values)
    {
        if (values is ProviderCredentialValues already)
        {
            return already;
        }

        if (values is null || values.Count == 0)
        {
            return None;
        }

        var held = values
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .ToFrozenDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        return held.Count == 0 ? None : new ProviderCredentialValues(held);
    }

    /// <summary>The one value a single-field mode stores, under the name that mode declares.</summary>
    /// <param name="name">The declared field name.</param>
    /// <param name="value">The value.</param>
    public static ProviderCredentialValues Single(string name, string value)
    {
        return From(new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value });
    }

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        return this._values.ContainsKey(key);
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, out string value)
    {
        return this._values.TryGetValue(key, out value!);
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        return this._values.GetEnumerator();
    }

    /// <summary>The field names present, without their values.</summary>
    public override string ToString()
    {
        return $"ProviderCredentialValues {{ Fields = {SecretSafeRendering.KeyNames(this._values)} }}";
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return this.GetEnumerator();
    }
}
