// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     The stored spellings a family supersedes, honoured when reading a row written before the family declared
///     its current names.
/// </summary>
/// <remarks>
///     <para>
///         Three axes carry a legacy spelling, not one. The identity key is the obvious case: rows written before
///         the key was declared hold the family's former name. The credential and wire-protocol axes carry one
///         too, because a declared mode persists qualified by the declaring family's key while rows written
///         earlier hold the unqualified name.
///     </para>
///     <para>
///         Honoured on read only, and only until the rows are rewritten. A family that has no rows to supersede
///         declares <see cref="None" />.
///     </para>
/// </remarks>
public sealed record ProviderLegacyNames
{
    /// <summary>
    ///     Private, so every instance arrives through a factory that has copied its collections. The type is
    ///     reachable by every add-in in the process, and one that cast a read-only dictionary back to
    ///     <see cref="Dictionary{TKey,TValue}" /> could add a spelling and change which stored rows another
    ///     family resolves.
    /// </summary>
    private ProviderLegacyNames(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> authModeSpellings,
        IReadOnlyDictionary<string, string> protocolModeSpellings)
    {
        this.Keys = keys;
        this.AuthModeSpellings = authModeSpellings;
        this.ProtocolModeSpellings = protocolModeSpellings;
    }

    /// <summary>The identity keys this family supersedes.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>For each declared authentication mode, the unqualified spelling it replaces.</summary>
    public IReadOnlyDictionary<string, string> AuthModeSpellings { get; }

    /// <summary>For each declared protocol mode, the unqualified spelling it replaces.</summary>
    public IReadOnlyDictionary<string, string> ProtocolModeSpellings { get; }

    /// <summary>
    ///     The names a family supersedes, stated one by one.
    /// </summary>
    /// <remarks>
    ///     Every collection is copied. A family that kept the one it passed and changed it afterwards would
    ///     otherwise change what the host resolves for every other family in the process.
    /// </remarks>
    /// <param name="keys">The identity keys this family supersedes.</param>
    /// <param name="authModeSpellings">For each authentication mode, the unqualified spelling it replaces.</param>
    /// <param name="protocolModeSpellings">For each protocol mode, the unqualified spelling it replaces.</param>
    public static ProviderLegacyNames Create(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> authModeSpellings,
        IReadOnlyDictionary<string, string> protocolModeSpellings)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(authModeSpellings);
        ArgumentNullException.ThrowIfNull(protocolModeSpellings);

        return new ProviderLegacyNames(
            [.. keys],
            authModeSpellings.ToImmutableDictionary(ProviderVocabulary.ValueComparer),
            protocolModeSpellings.ToImmutableDictionary(ProviderVocabulary.ValueComparer));
    }

    /// <summary>A family that supersedes nothing.</summary>
    /// <remarks>
    ///     The collections are immutable because a family's declaration is a process-wide instance that every
    ///     add-in in the process can reach: a caller that cast a read-only dictionary back to
    ///     <see cref="Dictionary{TKey,TValue}" /> could add a spelling and change which stored rows another
    ///     family resolves.
    /// </remarks>
    public static ProviderLegacyNames None { get; } = new(
        ImmutableArray<string>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, string>.Empty);

    /// <summary>
    ///     The spellings a family that qualified the mode names it already had supersedes: for each declared
    ///     mode, the mode name without its qualifier, which that family's rows hold until they are
    ///     rewritten.
    /// </summary>
    /// <remarks>
    ///     A mode carrying no qualifier is left out: the host-reserved protocol modes persist unqualified and
    ///     supersede nothing, so an entry for one would never be read. A family that renamed a mode rather than
    ///     only qualifying it states the superseded spelling itself, through the constructor.
    ///     <para>
    ///         The collections it builds are immutable for the reason given on <see cref="None" />, and the keys
    ///         are copied rather than held, so a caller that keeps its own array cannot edit the declaration
    ///         through it.
    ///     </para>
    /// </remarks>
    /// <param name="keys">Identity keys this family supersedes.</param>
    /// <param name="authModes">The authentication modes the family declares, as they persist.</param>
    /// <param name="protocolModes">The protocol modes the family declares, as they persist.</param>
    public static ProviderLegacyNames FromUnqualifiedNames(
        IReadOnlyList<string> keys,
        IReadOnlyList<string> authModes,
        IReadOnlyList<string> protocolModes)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(authModes);
        ArgumentNullException.ThrowIfNull(protocolModes);

        return new ProviderLegacyNames(
            keys.ToImmutableArray(),
            UnqualifiedSpellings(authModes),
            UnqualifiedSpellings(protocolModes));
    }

    private static ImmutableDictionary<string, string> UnqualifiedSpellings(IReadOnlyList<string> modes)
    {
        var spellings = ImmutableDictionary.CreateBuilder<string, string>(ProviderVocabulary.ValueComparer);

        foreach (var mode in modes)
        {
            var split = ProviderVocabulary.Split(mode);
            if (split.IsQualified)
            {
                spellings[mode] = split.ModeName;
            }
        }

        return spellings.ToImmutable();
    }
}
