// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     What one connection request carries for the fields its provider family declared, split into the values the
///     host persists in the settings document and the values it puts in the credential envelope.
/// </summary>
/// <param name="Settings">The non-secret values, by declared field name.</param>
/// <param name="Secrets">The secret-marked values, by declared field name.</param>
/// <param name="Refusals">What is wrong with the submission, each entry naming the field it is about.</param>
internal sealed record DeclaredValueSubmission(
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> Secrets,
    IReadOnlyList<KeyValuePair<string, string>> Refusals);

/// <summary>
///     Turns the declared half of a connection request into the values a family declared, and decides where each
///     one is kept.
/// </summary>
/// <remarks>
///     Shared by the client-scoped and tenant-scoped connection controllers, which accept the same request shape
///     and store through the same repository. Nothing here knows what a field means: which fields exist, what
///     shape each takes and which of them are credential material all come from the family's declaration.
/// </remarks>
internal static class AiConnectionDeclaredValues
{
    /// <summary>Collects the declared values one request carries.</summary>
    /// <param name="declaration">What the family declares.</param>
    /// <param name="submitted">The values the request carries, by field name.</param>
    /// <param name="stored">The non-secret values already stored on the connection, on an edit.</param>
    /// <param name="storedSecretNames">The secret-marked fields that already hold a stored value, on an edit.</param>
    public static DeclaredValueSubmission Collect(
        ProviderDeclaration declaration,
        IReadOnlyDictionary<string, string>? submitted,
        IReadOnlyDictionary<string, string>? stored = null,
        IReadOnlyList<string>? storedSecretNames = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var refusals = new List<KeyValuePair<string, string>>();
        var declaredFields = declaration.ConnectionFields;
        var visible = VisibleValues(declaredFields, submitted, stored);

        // A value under a name the family does not declare is a value an operator believes is in use and nothing
        // reads, so it is refused where the operator can see it rather than stored and ignored.
        foreach (var name in submitted?.Keys ?? [])
        {
            if (!declaredFields.Any(field => string.Equals(field.Name, name, StringComparison.Ordinal)))
            {
                refusals.Add(
                    new KeyValuePair<string, string>(
                        name,
                        $"'{declaration.Label}' declares no configuration field named '{name}'."));
            }
        }

        foreach (var field in declaredFields)
        {
            // A computed value is derived and recomputed wherever it is shown, so nothing a request carries for
            // one is kept. A field hidden by its visibility condition is left as it is stored, because a form that
            // does not show a field cannot have been asked about it.
            if (field.IsComputed || !visible.Contains(field.Name))
            {
                continue;
            }

            var entered = submitted is not null && submitted.TryGetValue(field.Name, out var value) ? value : null;
            if (entered is null)
            {
                // A secret box left empty on an edit means "keep what is stored", which the credential
                // boxes already mean. A required field with nothing stored and nothing entered is refused.
                if (field.IsRequired && !HasStoredValue(field, stored, storedSecretNames) && field.DefaultValue is null)
                {
                    refusals.Add(new KeyValuePair<string, string>(field.Name, $"{field.Label} is required."));
                }

                continue;
            }

            if (field.IsSecret)
            {
                // A blank secret is not a credential; treated as "not entered" so an operator who tabs through the
                // box does not replace a working value with an empty one.
                if (!string.IsNullOrWhiteSpace(entered))
                {
                    secrets[field.Name] = entered;
                }
                else if (field.IsRequired && !HasStoredValue(field, stored, storedSecretNames))
                {
                    refusals.Add(new KeyValuePair<string, string>(field.Name, $"{field.Label} is required."));
                }

                continue;
            }

            if (field.IsRequired && string.IsNullOrWhiteSpace(entered))
            {
                // A field the request carries empty is the same as one it does not carry, and a field the family
                // gave a default is not missing: the default is what the connection holds. A form submits every
                // input it renders, so a field shown empty arrives as a blank rather than as absent, and refusing
                // it here would refuse a connection the family stated a working value for.
                if (field.DefaultValue is null && !HasStoredValue(field, stored, storedSecretNames))
                {
                    refusals.Add(new KeyValuePair<string, string>(field.Name, $"{field.Label} is required."));
                }
                else if (field.DefaultValue is { } defaulted && !field.IsSecret)
                {
                    // Collected, so a probe runs against the value the saved connection will hold. The default
                    // is applied when a stored profile is projected, so omitting it here probed one endpoint and
                    // saved another, and a family whose default names the address was probed without it.
                    settings[field.Name] = defaulted;
                }

                continue;
            }

            // Stored unchecked, a value of the wrong shape is refused by the family at the first call of a
            // review rather than on the form the operator was filling in.
            if (ProviderFieldRules.DescribeShapeRefusal(field, entered) is { } shapeRefusal)
            {
                refusals.Add(new KeyValuePair<string, string>(field.Name, shapeRefusal));
                continue;
            }

            settings[field.Name] = entered;
        }

        return new DeclaredValueSubmission(settings, secrets, refusals);
    }

    /// <summary>The names of the fields whose visibility condition is met.</summary>
    /// <param name="declaredFields">The family's connection fields.</param>
    /// <param name="submitted">The values the request carries.</param>
    /// <param name="stored">The values already stored on the connection.</param>
    /// <remarks>
    ///     The condition is read against what the connection will hold: the submitted value of the deciding field
    ///     where the request carries one, its stored value otherwise, and the family's default where neither
    ///     exists. Reading it against the submission alone would hide a field whose deciding value is stored and
    ///     was not re-sent.
    ///     <para>
    ///         A stored secret is not among the values, because a secret is held encrypted and is never handed
    ///         back. A condition naming a secret field is therefore met only by a value entered in the same
    ///         request, which is as far as the host can read one.
    ///     </para>
    /// </remarks>
    public static IReadOnlySet<string> VisibleValues(
        IReadOnlyList<ProviderDeclaredField> declaredFields,
        IReadOnlyDictionary<string, string>? submitted,
        IReadOnlyDictionary<string, string>? stored)
    {
        ArgumentNullException.ThrowIfNull(declaredFields);

        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in declaredFields)
        {
            // A blank secret is read as "not entered", the same as where the values are collected. A form submits
            // every box it renders, so an edit that leaves the credential box alone arrives as a blank; treating
            // that blank as the deciding value would hide every field conditioned on it, and a hidden field is
            // neither validated nor collected, so the operator's other values would be dropped.
            var submittedValue = submitted is not null && submitted.TryGetValue(field.Name, out var entered)
                                                       && !(field.IsSecret && string.IsNullOrWhiteSpace(entered))
                ? entered
                : null;

            if (submittedValue is not null)
            {
                effective[field.Name] = submittedValue;
            }
            else if (stored is not null && stored.TryGetValue(field.Name, out var value))
            {
                effective[field.Name] = value;
            }
            else if (field.DefaultValue is not null)
            {
                effective[field.Name] = field.DefaultValue;
            }
        }

        return declaredFields
            .Where(field => ProviderFieldRules.IsVisible(field, effective, declaredFields))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasStoredValue(
        ProviderDeclaredField field,
        IReadOnlyDictionary<string, string>? stored,
        IReadOnlyList<string>? storedSecretNames)
    {
        return field.IsSecret
            ? storedSecretNames?.Contains(field.Name, StringComparer.Ordinal) == true
            : stored?.ContainsKey(field.Name) == true;
    }
}
