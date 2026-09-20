// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Conformance.Checks;

/// <summary>
///     A family declares one well-formed identity key, and declares the same one twice.
/// </summary>
/// <remarks>
///     The registry indexes by this key and every connection is stored against it, so a family whose key breaks a
///     token rule could not be stored, and one that changed its key between reads would shadow another.
/// </remarks>
internal sealed class FamilyIdentityCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "family-identity";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var key = subject.Driver.Declaration.Key;

        if (ProviderVocabulary.ValidateKey(key) is { } refusal)
        {
            return ConformanceResult.Fail(this.Name, refusal.Message);
        }

        return ProviderVocabulary.KeysEqual(key, subject.Driver.Declaration.Key)
            ? ConformanceResult.Pass(this.Name)
            : ConformanceResult.Fail(
                this.Name,
                "The family declared two different identity keys in two reads of the same declaration.");
    }
}

/// <summary>
///     What the family answers with is what it declared.
/// </summary>
/// <remarks>
///     The declaration is read before the driver is reached, so a family that declares one authentication mode and
///     accepts another offers an operator a configuration it cannot then use.
/// </remarks>
internal sealed class DeclaredVocabularyCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "vocabulary-matches-the-declaration";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var driver = subject.Driver;
        var declaration = driver.Declaration;

        if (!driver.SupportedAuthModes.SequenceEqual(declaration.SupportedAuthModes, ProviderVocabulary.ValueComparer))
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family declares the authentication modes {Rendered(declaration.SupportedAuthModes)} and answers "
                + $"with {Rendered(driver.SupportedAuthModes)}.");
        }

        // Each named mode has to be this family's to name. A mode persists as the declaring family's key joined
        // to the mode name, so a value qualified by another key is one no connection of this family could hold,
        // and one carrying no qualifier is a name two families could each mean something different by.
        if (Unclaimable(declaration.Key, driver.SupportedAuthModes, reservedAllowed: false) is { } unclaimableMode)
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family offers an authentication mode that is not its own to declare: {unclaimableMode}.");
        }

        if (!driver.SupportedProtocolModes.SequenceEqual(declaration.ProtocolModes.Supported, ProviderVocabulary.ValueComparer))
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family declares the wire protocols {Rendered(declaration.ProtocolModes.Supported)} and answers "
                + $"with {Rendered(driver.SupportedProtocolModes)}.");
        }

        if (Unclaimable(declaration.Key, driver.SupportedProtocolModes, reservedAllowed: true) is { } unclaimableProtocol)
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family speaks a wire protocol that is not its own to declare: {unclaimableProtocol}.");
        }

        var declaredFieldModes = declaration.CredentialFields.Keys.Order(StringComparer.Ordinal).ToList();
        var answeredFieldModes = driver.CredentialFields.Keys.Order(StringComparer.Ordinal).ToList();

        if (!declaredFieldModes.SequenceEqual(answeredFieldModes, ProviderVocabulary.ValueComparer))
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family declares credential fields for {Rendered(declaredFieldModes)} and answers with fields "
                + $"for {Rendered(answeredFieldModes)}.");
        }

        // The fields themselves, not the authentication modes they are keyed by. The declaration is what a
        // console renders the form from and the driver's answer is what the credential is collected and read
        // back against, so a family whose two sides name different fields for one mode offers boxes the driver
        // never reads.
        foreach (var mode in answeredFieldModes)
        {
            if (DescribeFieldMismatch(declaration.CredentialFields[mode], driver.CredentialFields[mode]) is { } detail)
            {
                return ConformanceResult.Fail(
                    this.Name,
                    $"The family declares and answers with different credential fields for '{mode}': {detail}");
            }
        }

        return ConformanceResult.Pass(this.Name);
    }

    /// <summary>The first declared value this family cannot own, and why, or null when every one is its own.</summary>
    /// <param name="key">The family's declared identity key.</param>
    /// <param name="modes">The modes the family answered with.</param>
    /// <param name="reservedAllowed">
    ///     Whether a host-reserved value is permitted, which the wire-protocol axis has and the credential axis
    ///     does not.
    /// </param>
    private static string? Unclaimable(string key, IEnumerable<string> modes, bool reservedAllowed)
    {
        foreach (var mode in modes)
        {
            if (reservedAllowed && ProviderDeclaredProtocolModes.IsReserved(mode))
            {
                continue;
            }

            if (ProviderVocabulary.ValidateQualifiedValue(mode) is { } refusal)
            {
                return refusal.Message;
            }

            if (!ProviderVocabulary.KeysEqual(ProviderVocabulary.Split(mode).Key, key))
            {
                return $"the value '{mode}' is qualified by another family's key, and what a mode name means is "
                       + "the declaring family's to say";
            }
        }

        return null;
    }

    /// <summary>How one mode's declared fields differ from the ones the driver answers with, or null.</summary>
    /// <param name="declared">The fields as the declaration states them.</param>
    /// <param name="answered">The fields as the driver answers with them.</param>
    private static string? DescribeFieldMismatch(
        IReadOnlyList<ProviderCredentialField> declared,
        IReadOnlyList<ProviderCredentialField> answered)
    {
        if (declared.Count != answered.Count)
        {
            return $"it declares {declared.Count} field(s) and answers with {answered.Count}.";
        }

        for (var index = 0; index < declared.Count; index++)
        {
            if (declared[index] != answered[index])
            {
                return $"field {index} is declared as {declared[index]} and answered as {answered[index]}.";
            }
        }

        return null;
    }

    private static string Rendered<T>(IEnumerable<T> values)
    {
        var rendered = string.Join(", ", values.Select(value => $"'{value}'"));
        return rendered.Length == 0 ? "none" : rendered;
    }
}

/// <summary>
///     A family speaks at least one wire protocol, and always speaks Auto.
/// </summary>
/// <remarks>
///     Auto is what a binding says when it has no opinion, which is the common case, so a family that cannot
///     serve it can never be selected by default.
/// </remarks>
internal sealed class ProtocolShapeCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "protocol-modes";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var supported = subject.Driver.SupportedProtocolModes;

        if (supported.Count == 0)
        {
            return ConformanceResult.Fail(this.Name, "The family speaks no wire protocol, so no binding can reach it.");
        }

        if (!ProviderVocabulary.Names(supported, ProviderDeclaredProtocolModes.Auto))
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family does not speak '{ProviderDeclaredProtocolModes.Auto}', which a binding names "
                + "when it has no opinion, so it can never be selected by default.");
        }

        return supported.Distinct(ProviderVocabulary.ValueComparer).Count() == supported.Count
            ? ConformanceResult.Pass(this.Name)
            : ConformanceResult.Fail(this.Name, "The family names a wire protocol more than once.");
    }
}

/// <summary>
///     A family declares at least one authentication mode, each of them once.
/// </summary>
/// <remarks>
///     A family with no authentication mode cannot be configured at all, and the registry refuses to be built from
///     one; checked here as well so a family that stopped declaring is caught by its own build rather than by a
///     host failing to start.
/// </remarks>
internal sealed class CredentialShapeCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "authentication-modes";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var supported = subject.Driver.SupportedAuthModes;

        if (supported.Count == 0)
        {
            return ConformanceResult.Fail(this.Name, "The family declares no authentication mode, so it cannot be configured.");
        }

        return supported.Distinct(ProviderVocabulary.ValueComparer).Count() == supported.Count
            ? ConformanceResult.Pass(this.Name)
            : ConformanceResult.Fail(this.Name, "The family names an authentication mode more than once.");
    }
}

/// <summary>
///     Every authentication mode a family offers declares the fields it needs.
/// </summary>
/// <remarks>
///     An authentication mode with no field declaration is offered and then collects nothing, so the profile is
///     saved without a credential.
/// </remarks>
internal sealed class CredentialFieldCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "credential-fields";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var driver = subject.Driver;

        foreach (var mode in driver.SupportedAuthModes)
        {
            if (!driver.CredentialFields.TryGetValue(mode, out var fields))
            {
                return ConformanceResult.Fail(this.Name, $"The authentication mode '{mode}' is offered without its fields.");
            }

            if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Count)
            {
                return ConformanceResult.Fail(this.Name, $"The authentication mode '{mode}' names a field more than once.");
            }

            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Label))
                {
                    return ConformanceResult.Fail(
                        this.Name,
                        $"The authentication mode '{mode}' declares a field with no name or no label, which the host "
                        + "cannot collect or render.");
                }
            }
        }

        return ConformanceResult.Pass(this.Name);
    }
}

/// <summary>
///     A family declares credential fields only for the authentication modes it offers.
/// </summary>
/// <remarks>
///     Fields for a mode the family does not offer are inputs nothing can select, and the likely cause is a mode
///     that was withdrawn without its fields.
/// </remarks>
internal sealed class CredentialFieldScopeCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "credential-fields-match-auth-modes";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var driver = subject.Driver;

        var stranded = driver.CredentialFields.Keys
            .Where(mode => !ProviderVocabulary.Names(driver.SupportedAuthModes, mode))
            .Select(mode => $"'{mode}'")
            .ToList();

        return stranded.Count == 0
            ? ConformanceResult.Pass(this.Name)
            : ConformanceResult.Fail(
                this.Name,
                $"The family declares fields for the authentication mode(s) {string.Join(", ", stranded)}, which it does "
                + "not offer.");
    }
}

/// <summary>
///     Every declared field name a declaration refers to resolves to a field the family declared.
/// </summary>
/// <remarks>
///     A name that resolves to nothing is not reported anywhere at runtime: the host looks it up by exact
///     ordinal comparison and carries on with its own default, so an invocation window the family narrowed stays
///     at the host maximum and a field the family meant to hide stays shown. Both read as the feature not
///     working rather than as a declaration naming something that is not there.
/// </remarks>
internal sealed class DeclaredFieldReferenceCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "declared-field-references";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var declaration = subject.Driver.Declaration;
        var unresolved = new List<string>();

        if (declaration.InvocationWindow?.DerivedFromFieldName is { } windowField)
        {
            var declared = declaration.ConnectionFields
                .FirstOrDefault(field => string.Equals(field.Name, windowField, StringComparison.Ordinal));

            if (declared is null)
            {
                unresolved.Add(
                    $"the invocation window is derived from '{windowField}', which is not a declared connection "
                    + "field");
            }
            else if (declared.Kind != ProviderFieldKind.Int)
            {
                unresolved.Add(
                    $"the invocation window is derived from '{windowField}', which is declared as "
                    + $"'{declared.Kind}'. The host reads it as a whole number of seconds");
            }
        }

        foreach (var field in declaration.Fields.Where(field => field.VisibleWhen is not null))
        {
            var deciding = field.VisibleWhen!.FieldName;
            if (!declaration.Fields.Any(other =>
                    other.Scope == field.Scope && string.Equals(other.Name, deciding, StringComparison.Ordinal)))
            {
                unresolved.Add(
                    $"'{field.Name}' is shown when '{deciding}' holds a value, and '{deciding}' is not a declared "
                    + $"field of the same scope");
            }
        }

        return unresolved.Count == 0
            ? ConformanceResult.Pass(this.Name)
            : ConformanceResult.Fail(
                this.Name,
                $"The declaration refers to field names that resolve to nothing: {string.Join("; ", unresolved)}.");
    }
}

/// <summary>
///     The authentication mode the rest of the checks present is one the family offers.
/// </summary>
/// <remarks>
///     Every case below it would otherwise be measuring a combination the family never claimed to serve.
/// </remarks>
internal sealed class ConformanceCredentialShapeCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "declared-authentication-mode";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var mode = subject.ResolvedInputs.CredentialAuthMode;

        return ProviderVocabulary.Names(subject.Driver.SupportedAuthModes, mode)
            ? ConformanceResult.Pass(this.Name)
            : ConformanceResult.Fail(
                this.Name,
                $"The checks are told to present the authentication mode '{mode}', which this family does not offer.");
    }
}
