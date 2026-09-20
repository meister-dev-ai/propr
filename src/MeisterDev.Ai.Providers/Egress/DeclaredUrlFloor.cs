// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     The check every address a provider family declared passes before the family's own validation runs, and
///     again before the value is stored.
/// </summary>
/// <remarks>
///     <para>
///         A family may refuse more than the installation does — an endpoint that is not its vendor's, a path it
///         cannot serve — and it may not admit what the installation refuses. Left to each family, the rule would
///         be re-implemented once per family and would go missing the first time one forgot it, turning a value an
///         operator typed into egress the installation never permitted.
///     </para>
///     <para>
///         Only a value whose declared shape is an address is checked as one. A family that declares a field as
///         free text and then uses it as an address has put the value somewhere the host does not read as an
///         address, and the host never treats it as one.
///     </para>
/// </remarks>
public static class DeclaredUrlFloor
{
    /// <summary>
    ///     Why the installation refuses the addresses among <paramref name="values" />, each entry keyed to the
    ///     declared field it is about. Empty when every address is permitted.
    /// </summary>
    /// <param name="declaration">What the family declares, which says which values are addresses.</param>
    /// <param name="values">The values as the operator entered them, by declared field name.</param>
    /// <param name="policy">What this installation permits an address to reach.</param>
    /// <remarks>
    ///     A field the request carries no value for is not checked: there is no address to refuse, and whether the
    ///     field had to be filled in is the required rule's question rather than this one's.
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> FindRefusedAddressFields(
        ProviderDeclaration declaration,
        IReadOnlyDictionary<string, string> values,
        EgressUrlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(policy);

        var refusals = new List<KeyValuePair<string, string>>();

        foreach (var field in declaration.Fields)
        {
            if (field.Kind != ProviderFieldKind.Url
                || !values.TryGetValue(field.Name, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (GetRefusalReason(declaration, field, value, policy) is { } refusal)
            {
                refusals.Add(new KeyValuePair<string, string>(field.Name, refusal));
            }
        }

        return refusals;
    }

    /// <summary>
    ///     Why the installation refuses one declared address, or <see langword="null" /> when it permits it.
    /// </summary>
    /// <param name="declaration">What the family declares, which carries the co-location statement.</param>
    /// <param name="field">The field the value was entered into, which names the refusal.</param>
    /// <param name="value">The address as the operator entered it.</param>
    /// <param name="policy">What this installation permits an address to reach.</param>
    public static string? GetRefusalReason(
        ProviderDeclaration declaration,
        ProviderDeclaredField field,
        string? value,
        EgressUrlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(policy);

        return field.Kind != ProviderFieldKind.Url
            ? null
            : policy.GetRefusalReason(value, field.Label, declaration.RequiresBrowserCoLocation);
    }
}
