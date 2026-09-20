// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Words the refusal for an authentication mode a family does not declare, and composes the one mode most
///     families declare.
/// </summary>
/// <remarks>
///     <para>
///         Which modes a family serves is the family's own declaration, in <c>AuthModes</c>. This class decides
///         nothing about that; it compares a requested mode against the declared list and writes the sentence an
///         operator reads when the two do not match, so the sentence is the same whichever family produced it.
///     </para>
///     <para>
///         A connection can hold a shape its family does not serve — a profile stored before the family declared
///         its shapes, or one whose shape was edited. Without a refusal the credential is written where that
///         provider does not read it, or the shape is ignored and a different credential is sent, and the call
///         fails as an authentication error naming neither the shape nor the family.
///     </para>
///     <para>
///         The fields one shape collects are a different question, answered by
///         <see cref="AiCredentialFieldSupport" /> and by <c>ProviderDeclaration.CredentialFieldsFor</c>. This
///         class is about which shapes exist; that one is about what each collects.
///     </para>
/// </remarks>
public static class AiAuthModeSupport
{
    /// <summary>
    ///     The single authentication mode an endpoint reached with a bearer key can use, qualified by the family
    ///     declaring it. Shared by the drivers that speak the OpenAI protocol, which send the stored key as
    ///     <c>Authorization: Bearer</c> and have nowhere else to put a credential.
    /// </summary>
    /// <remarks>
    ///     Composed against the caller's own key rather than shared as one instance, because an authentication mode
    ///     belongs to the family that declares it: two families naming <c>ApiKey</c> name two shapes, and the
    ///     qualifier is what keeps one family's credential from being read as the other's. The result is an
    ///     <see cref="ImmutableArray{T}" /> so a caller cannot cast it back to an array and change what the
    ///     driver holding it declares; the return type stays <see cref="IReadOnlyList{T}" /> because a driver
    ///     compiled outside this repository links against the declared type.
    /// </remarks>
    /// <param name="key">The identity key of the family declaring the shape.</param>
    public static IReadOnlyList<string> ApiKeyOnly(string key)
    {
        return ImmutableArray.Create(ProviderVocabulary.Compose(key, "ApiKey"));
    }

    /// <summary>
    ///     Returns a user-facing reason when <paramref name="requested" /> is not one of
    ///     <paramref name="supported" />, or <see langword="null" /> when it is.
    /// </summary>
    /// <param name="providerKind">The provider family being asked, named in the reason.</param>
    /// <param name="supported">The authentication modes that driver can authenticate with.</param>
    /// <param name="requested">The authentication mode being asked for.</param>
    public static string? GetRefusalReason(
        string providerKind,
        IReadOnlyList<string> supported,
        string requested)
    {
        ArgumentNullException.ThrowIfNull(supported);

        return ProviderVocabulary.Names(supported, requested)
            ? null
            : $"the '{providerKind}' provider does not authenticate with '{requested}' "
              + $"(it authenticates with: {string.Join(", ", supported)})";
    }
}
