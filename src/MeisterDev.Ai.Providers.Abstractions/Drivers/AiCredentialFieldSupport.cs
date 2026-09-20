// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     The credential field most families declare, and the wording of a refusal about the fields one mode
///     collects.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="AiAuthModeSupport" /> answers which authentication modes a family declares. This class
///         answers what one mode collects: an API key, three fields, a JSON document, or nothing at all.
///     </para>
///     <para>
///         A host collects whatever a driver declares and never learns what a field means, so it cannot word a
///         refusal on its own. It knows only that a required value is absent or that a name is not in the
///         declaration. Wording both here keeps the refusal the same whichever family produced it, and keeps the
///         field names in the message the same names the driver reads back.
///     </para>
/// </remarks>
public static class AiCredentialFieldSupport
{
    /// <summary>The single field a mode whose credential is one key needs.</summary>
    public static ProviderCredentialField ApiKey { get; } =
        new(ProviderCredentialField.ApiKeyFieldName, "API key");

    /// <summary>The declaration for a mode that needs no stored credential, such as an ambient identity.</summary>
    public static ImmutableArray<ProviderCredentialField> None { get; } = [];

    /// <summary>
    ///     Returns a user-facing reason for every required field <paramref name="supplied" /> is missing and every
    ///     name it carries that <paramref name="declared" /> does not, or an empty list when it matches.
    /// </summary>
    /// <remarks>
    ///     An undeclared name is reported and not dropped: it is a value an operator entered and expects to be in
    ///     use, and storing it silently would leave a credential in the row that nothing reads. Each reason is a
    ///     whole sentence, because it is reported on its own and not folded into a wider refusal.
    /// </remarks>
    /// <param name="providerKind">The provider family being configured, named in each reason.</param>
    /// <param name="mode">The authentication mode being configured, named in each reason.</param>
    /// <param name="declared">The fields that mode needs.</param>
    /// <param name="supplied">The values that were entered. Blank ones are already dropped by the type.</param>
    public static IReadOnlyList<string> FindCredentialFieldRefusals(
        string providerKind,
        string mode,
        IReadOnlyList<ProviderCredentialField> declared,
        ProviderCredentialValues supplied)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(supplied);

        var refusals = new List<string>();

        foreach (var field in declared.Where(field => field.IsRequired && !supplied.ContainsKey(field.Name)))
        {
            refusals.Add(
                $"The '{providerKind}' provider needs '{field.Label}' ('{field.Name}') "
                + $"for '{mode}' authentication.");
        }

        var names = declared.Select(field => field.Name).ToList();
        foreach (var name in supplied.Keys.Where(name => !names.Contains(name, StringComparer.Ordinal)))
        {
            refusals.Add(
                $"The '{providerKind}' provider has no '{name}' credential field for '{mode}' authentication "
                + $"(it takes: {(names.Count == 0 ? "no credential" : string.Join(", ", names))}).");
        }

        return refusals;
    }

    /// <summary>
    ///     Reads a stored credential under the names <paramref name="declared" /> uses.
    /// </summary>
    /// <remarks>
    ///     A credential of one field is stored as its value alone, so the name it was written under does not
    ///     survive the round trip and comes back under <see cref="ProviderCredentialField.ApiKeyFieldName" />.
    ///     Reading that value under the declared name lets a profile be edited without re-entering the
    ///     credential. A value that came back under any other name is material a driver stored deliberately:
    ///     renaming it into a different field would present one family's credential as another's, so it is
    ///     returned unchanged and refused as a field the mode does not declare.
    /// </remarks>
    /// <param name="declared">The fields the mode needs.</param>
    /// <param name="stored">The values read back from storage.</param>
    public static ProviderCredentialValues Adopt(
        IReadOnlyList<ProviderCredentialField> declared,
        ProviderCredentialValues stored)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(stored);

        if (declared.Count != 1
            || declared[0].Name == ProviderCredentialField.ApiKeyFieldName
            || stored.Count != 1
            || !stored.TryGetValue(ProviderCredentialField.ApiKeyFieldName, out var value))
        {
            return stored;
        }

        return ProviderCredentialValues.Single(declared[0].Name, value);
    }
}
