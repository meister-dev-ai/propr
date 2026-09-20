// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Reads a stored identity, authentication mode or protocol mode against what the loaded families declare,
///     including the spellings each family supersedes.
/// </summary>
/// <remarks>
///     <para>
///         A family moves onto its declared identity key and its qualified mode names before its rows are
///         rewritten. Between those two moments every connection of that family holds values the declared names
///         do not match, and each one would be reported as unavailable in the middle of the change meant to keep
///         it working. The declared legacy spellings close that window, on read only: nothing is written back
///         through an alias, so a row keeps its value until its own rewrite.
///     </para>
///     <para>
///         A mode is resolved against one family, because the row that holds it already names the family it
///         belongs to. Two families superseding the same unqualified spelling is therefore not ambiguous, and
///         every shipped family supersedes <c>ApiKey</c>. An identity is different: the stored value is the only
///         thing that says which family the row belongs to, so a legacy identity key claimed by two families is
///         refused where the registry is built.
///     </para>
/// </remarks>
public static class AiProviderLegacyNames
{
    /// <summary>
    ///     Resolves a stored provider identity against the loaded families: each family's declared key, then the
    ///     keys each family supersedes.
    /// </summary>
    /// <param name="registry">The loaded families.</param>
    /// <param name="identity">The identity as it was read.</param>
    /// <returns>The family the identity names, or an unresolved outcome.</returns>
    /// <remarks>
    ///     The loaded families are the whole set of identities, so an identity nothing claims is unresolved
    ///     however well formed it is. Matching folds case, because that is how two keys are compared everywhere
    ///     else; the identity is reported in the spelling it was read under, and the claiming family's declared
    ///     key is reported beside it.
    /// </remarks>
    public static AiProviderIdentityResolution ResolveIdentity(
        this IAiProviderDriverRegistry registry,
        string? identity)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var trimmed = identity?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return AiProviderIdentityResolution.Unresolved(trimmed);
        }

        foreach (var providerKind in registry.RegisteredKinds)
        {
            var declaration = registry.GetRequired(providerKind).Declaration;
            var claimed = ProviderVocabulary.KeysEqual(declaration.Key, trimmed)
                          || declaration.LegacyNames.Keys.Any(key => ProviderVocabulary.KeysEqual(key, trimmed));

            if (claimed)
            {
                return AiProviderIdentityResolution.Resolved(trimmed, declaration.Key);
            }
        }

        return AiProviderIdentityResolution.Unresolved(trimmed);
    }

    /// <summary>
    ///     Resolves a stored authentication mode carried by a connection of <paramref name="providerKind" />.
    /// </summary>
    /// <param name="registry">The loaded families.</param>
    /// <param name="providerKind">The family the row belongs to, or null where no family can be determined.</param>
    /// <param name="stored">The value as it was read.</param>
    public static AiVocabularyResolution ResolveAuthMode(
        this IAiProviderDriverRegistry registry,
        string? providerKind,
        string? stored)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return Resolve(
            registry,
            providerKind,
            stored,
            isReserved: _ => false,
            legacy => legacy.AuthModeSpellings,
            declaration => declaration.SupportedAuthModes);
    }

    /// <summary>
    ///     Resolves a stored protocol mode carried by a connection of <paramref name="providerKind" />.
    /// </summary>
    /// <param name="registry">The loaded families.</param>
    /// <param name="providerKind">The family the row belongs to, or null where no family can be determined.</param>
    /// <param name="stored">The value as it was read.</param>
    /// <remarks>
    ///     The host-reserved shapes belong to no family, so they resolve whether or not a family was named. That
    ///     is what lets a row holding the default be read where the family it maps to cannot be determined.
    /// </remarks>
    public static AiVocabularyResolution ResolveProtocolMode(
        this IAiProviderDriverRegistry registry,
        string? providerKind,
        string? stored)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return Resolve(
            registry,
            providerKind,
            stored,
            ProviderDeclaredProtocolModes.IsReserved,
            legacy => legacy.ProtocolModeSpellings,
            declaration => declaration.ProtocolModes.Supported);
    }

    // One resolution order for both mode axes:
    //   a qualified value names the family that declared the mode, and a value qualified by another family's key
    //   does not resolve here at all;
    //   a host-reserved shape persists unqualified and is owned by nobody, so no family can shadow it;
    //   a spelling this family supersedes resolves to the mode that replaced it;
    //   and otherwise nothing claims the value, because the loaded families are the whole set of modes.
    // What is reported is the value as it was read, and what it resolves to is the spelling the mode persists
    // under, so a caller comparing two modes compares whole qualified values and never a mode name on its own.
    private static AiVocabularyResolution Resolve(
        IAiProviderDriverRegistry registry,
        string? providerKind,
        string? stored,
        Func<string, bool> isReserved,
        Func<ProviderLegacyNames, IReadOnlyDictionary<string, string>> supersededSpellings,
        Func<ProviderDeclaration, IReadOnlyList<string>> declaredModes)
    {
        var trimmed = stored?.Trim() ?? string.Empty;
        var split = ProviderVocabulary.Split(trimmed);
        var declaration = registry.IsRegistered(providerKind)
            ? registry.GetRequired(providerKind).Declaration
            : null;

        if (split.IsQualified)
        {
            return declaration is not null
                   && ProviderVocabulary.KeysEqual(declaration.Key, split.Key)
                   && Declared(declaredModes(declaration), trimmed) is { } declared
                ? AiVocabularyResolution.Resolved(trimmed, declared)
                : AiVocabularyResolution.Unresolved(trimmed);
        }

        if (isReserved(trimmed))
        {
            return AiVocabularyResolution.Resolved(trimmed, Reserved(trimmed));
        }

        if (declaration is not null)
        {
            foreach (var (mode, spelling) in supersededSpellings(declaration.LegacyNames))
            {
                if (ProviderVocabulary.ValuesEqual(spelling, trimmed))
                {
                    return AiVocabularyResolution.Resolved(trimmed, mode);
                }
            }
        }

        return AiVocabularyResolution.Unresolved(trimmed);
    }

    // The family's own spelling of a mode it declares, or null where it declares no such mode. Returned rather
    // than the value as read, so a row written in another casing resolves to the one spelling the family states.
    private static string? Declared(IReadOnlyList<string> declaredModes, string value)
    {
        foreach (var mode in declaredModes)
        {
            if (ProviderVocabulary.ValuesEqual(mode, value))
            {
                return mode;
            }
        }

        return null;
    }

    // The host's own spelling of a reserved shape, for the same reason a declared mode is reported in the
    // family's spelling.
    private static string Reserved(string value)
    {
        return ProviderDeclaredProtocolModes.Reserved
            .First(reserved => ProviderVocabulary.ValuesEqual(reserved, value));
    }
}
