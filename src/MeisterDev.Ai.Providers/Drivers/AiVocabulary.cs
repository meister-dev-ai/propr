// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Reads a stored or transported value against the members a host-owned enumeration declares, without
///     throwing on a value this build has no member for.
/// </summary>
/// <remarks>
///     The columns these values are read from hold names in unconstrained text, and a value written by a build
///     that declared a member this one does not is therefore already storable. An enum parse turns such a value
///     into an exception in the middle of a projection, which takes out every other row being projected with it.
///     Resolving instead returns the value unresolved, so the caller reports it and keeps going.
///     <para>
///         For the axes a provider family names — the authentication mode and the protocol mode — the set of valid
///         values is what the loaded families declared rather than an enumeration, and
///         <see cref="AiProviderLegacyNames" /> reads those.
///     </para>
/// </remarks>
public static class AiVocabulary
{
    /// <summary>Resolves a stored or transported value against a host-owned enumeration.</summary>
    /// <typeparam name="TEnum">The enumeration to read the value against.</typeparam>
    /// <param name="value">The value as it was read; whitespace around it is ignored.</param>
    /// <returns>The member the value names, or an unresolved outcome for a value this build cannot name.</returns>
    /// <remarks>
    ///     Matching ignores case, as the enum parses this replaces did. It accepts declared names only: a numeric
    ///     string and a comma-separated list both name no member, and an enum parse accepted both — the first as
    ///     whatever member carries that number, the second as the bitwise combination of two.
    /// </remarks>
    public static AiVocabularyResolution<TEnum> Resolve<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var trimmed = value?.Trim() ?? string.Empty;

        return Vocabulary<TEnum>.MembersByName.TryGetValue(trimmed, out var member)
            ? AiVocabularyResolution<TEnum>.Resolved(trimmed, member)
            : AiVocabularyResolution<TEnum>.Unresolved(trimmed);
    }

    // Built once per enumeration. The names are the enum member names, which was written to the database
    // and reported on the wire, so reading through this type leaves every stored value as it was.
    private static class Vocabulary<TEnum>
        where TEnum : struct, Enum
    {
        internal static readonly FrozenDictionary<string, TEnum> MembersByName = BuildMembersByName();

        // Built from the declared names, not from the values. An alias declares two names for one value, and
        // building from the values keeps only the name ToString returns for it, which would report the other
        // name as unresolved although this build declares it.
        private static FrozenDictionary<string, TEnum> BuildMembersByName()
        {
            var membersByName = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in Enum.GetNames<TEnum>())
            {
                // Two names that differ only by case are both declared, and a case-insensitive lookup holds one
                // of them. The first the enum reports wins, so a vocabulary declaring such a pair resolves
                // instead of throwing out of the type initializer, where the failure would reach the caller as
                // a type-initialisation exception and not the unresolved outcome Resolve documents.
                membersByName.TryAdd(name, Enum.Parse<TEnum>(name));
            }

            return membersByName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
    }
}
