// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     What the host checks about a value an operator entered into a field a family declared: that it has the
///     declared shape, and whether the field is shown at all.
/// </summary>
/// <remarks>
///     The declared shape is a promise the host makes to a family: a family reading an
///     <see cref="ProviderFieldKind.Int" /> field parses it without checking, and a family switching on a
///     <see cref="ProviderFieldKind.Choice" /> field has a case per option it declared. The promise holds for a
///     connection field and for an action input, so both paths ask here.
/// </remarks>
public static class ProviderFieldRules
{
    /// <summary>Why the value does not have the declared shape, or <see langword="null" /> when it does.</summary>
    /// <remarks>
    ///     Addresses are not checked: the egress floor and the family's own validator answer for those, and both
    ///     name the field they refuse.
    /// </remarks>
    /// <param name="field">The field the value was entered into.</param>
    /// <param name="entered">The value as the operator entered it.</param>
    public static string? DescribeShapeRefusal(ProviderDeclaredField field, string entered)
    {
        ArgumentNullException.ThrowIfNull(field);

        // A blank optional value is the field left empty, which is not a value of the wrong shape.
        if (string.IsNullOrWhiteSpace(entered))
        {
            return null;
        }

        return field.Kind switch
        {
            ProviderFieldKind.Int when !long.TryParse(entered, out _) =>
                $"{field.Label} takes a whole number.",
            ProviderFieldKind.Bool when !bool.TryParse(entered, out _) =>
                $"{field.Label} takes 'true' or 'false'.",
            ProviderFieldKind.Choice when !field.Choices.Contains(entered, StringComparer.Ordinal) =>
                field.Choices.Count == 0
                    ? $"{field.Label} offers no options to choose from."
                    : $"{field.Label} takes one of {string.Join(", ", field.Choices.Select(choice => $"'{choice}'"))}.",
            _ => null,
        };
    }

    /// <summary>Whether one field's visibility condition is met by <paramref name="values" />.</summary>
    /// <param name="field">The field to decide about.</param>
    /// <param name="values">The value of every declared field, as the submission will leave it.</param>
    /// <param name="declaredFields">
    ///     The family's fields, used to read the shape of the deciding field. Without them the comparison is
    ///     textual, which a caller holding no declaration can do.
    /// </param>
    public static bool IsVisible(
        ProviderDeclaredField field,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<ProviderDeclaredField>? declaredFields = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);

        if (field.VisibleWhen is not { } condition)
        {
            return true;
        }

        if (!values.TryGetValue(condition.FieldName, out var deciding))
        {
            return false;
        }

        // Compared the way the shape rules accept the value, not as typed. Shape validation takes TRUE for a
        // boolean and 007 for an integer, so an ordinal comparison against the declared condition hid a field
        // whose deciding value was accepted everywhere else.
        var decider = declaredFields?.FirstOrDefault(candidate => string.Equals(candidate.Name, condition.FieldName, StringComparison.Ordinal));

        return decider?.Kind switch
        {
            ProviderFieldKind.Bool =>
                bool.TryParse(deciding, out var left)
                && bool.TryParse(condition.EqualsValue, out var right)
                && left == right,
            ProviderFieldKind.Int =>
                long.TryParse(deciding, out var leftNumber)
                && long.TryParse(condition.EqualsValue, out var rightNumber)
                && leftNumber == rightNumber,
            _ => string.Equals(deciding, condition.EqualsValue, StringComparison.Ordinal),
        };
    }
}
