// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Turns the credential half of a connection request into the fields a driver declared, and those fields into
///     the one value that is stored.
/// </summary>
/// <remarks>
///     Shared by the client-scoped and tenant-scoped connection controllers, which accept the same request shape
///     and store through the same repository. Nothing here knows what a field means: which fields exist comes
///     from the driver's declaration, and what is done with them is the driver's business at call time.
/// </remarks>
internal static class AiConnectionCredential
{
    /// <summary>
    ///     Collects the credential fields a request carries, dropping the ones left empty.
    /// </summary>
    /// <remarks>
    ///     A blank value is dropped: a form submits every field it renders, and an empty one means the operator
    ///     entered nothing. The single <c>apiKey</c> property is folded in under that name, so
    ///     a caller that only ever sent a key keeps working unchanged; an explicit field of the same name wins,
    ///     because it is the more specific of the two.
    ///     <para>
    ///         A name is taken exactly as it was submitted. Trimming it would let <c>' name '</c> and
    ///         <c>'name'</c> arrive as one entry, with whichever came last silently displacing the other; kept as
    ///         submitted, a padded name matches no declared field and is refused naming it.
    ///     </para>
    /// </remarks>
    /// <param name="auth">The credential half of the request.</param>
    public static Dictionary<string, string> Collect(AiConnectionAuthRequest auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        var collected = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in auth.Fields ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
            {
                collected[name] = value.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(auth.ApiKey))
        {
            collected.TryAdd(ProviderSecretEnvelope.ApiKeyField, auth.ApiKey.Trim());
        }

        return collected;
    }

    /// <summary>
    ///     Why the host refuses to store the collected fields, or an empty list when it will store them.
    /// </summary>
    /// <remarks>
    ///     An operator-entered credential is held to the bound a provider family's own credential is held to,
    ///     stated once in <see cref="ProviderHostLimits" />, because both are written into the same protected
    ///     column. Without this the operator route carries no bound: the fields are encoded into one string and
    ///     the column takes any length, leaving the request-body limit of whichever host received it.
    /// </remarks>
    /// <param name="fields">The collected fields.</param>
    public static IReadOnlyList<string> FindCredentialSizeRefusals(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var refusals = new List<string>();

        if (fields.Count > ProviderHostLimits.MaximumCredentialFieldCount)
        {
            refusals.Add(
                $"The credential carries {fields.Count} fields; at most "
                + $"{ProviderHostLimits.MaximumCredentialFieldCount} are stored.");
        }

        foreach (var (name, value) in fields)
        {
            if (value.Length > ProviderHostLimits.MaximumCredentialFieldLength)
            {
                refusals.Add(
                    $"The credential field '{name}' is {value.Length} characters; at most "
                    + $"{ProviderHostLimits.MaximumCredentialFieldLength} are stored.");
            }
        }

        return refusals;
    }

    /// <summary>
    ///     Encodes the collected fields as the single protected value a profile stores, or <see langword="null" />
    ///     for a mode that needs no credential.
    /// </summary>
    /// <param name="mode">The authentication mode the fields belong to.</param>
    /// <param name="fields">The collected fields.</param>
    public static string? Encode(string mode, IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        return fields.Count == 0 ? null : new ProviderSecretEnvelope(mode, fields).Encode();
    }
}
