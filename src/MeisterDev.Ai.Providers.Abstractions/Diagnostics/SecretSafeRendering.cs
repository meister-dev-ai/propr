// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;

namespace MeisterDev.Ai.Providers.Diagnostics;

/// <summary>
///     Helpers for rendering credential-bearing types without their credentials.
/// </summary>
/// <remarks>
///     <para>
///         A record's generated <c>ToString</c> prints every property, so any type holding a secret leaks it the
///         first time someone interpolates it into a message or a log line. It needs no logging configuration to
///         go wrong and no code review to notice, because the call site looks ordinary. Types that hold secrets
///         override <c>ToString</c> using these helpers, so the safe rendering is the default and no call site
///         has to remember it.
///     </para>
///     <para>
///         This covers the <c>ToString</c> path only. A log call that destructures an object instead, Serilog's
///         <c>{@value}</c>, reflects over the properties and never consults <c>ToString</c>; that path is closed
///         on the host side by the transforms in <c>AiConnectionLogRedaction</c> and the destructuring policy
///         registered under them. .NET has no equivalent for this path: a record's generated <c>ToString</c> has
///         no opt-out attribute.
///     </para>
/// </remarks>
public static class SecretSafeRendering
{
    /// <summary>
    ///     The member names credential material is held under.
    /// </summary>
    /// <remarks>
    ///     This set is what a provider family is judged by, so a name added here newly refuses a family whose
    ///     type carries it. The product's own types are judged by <see cref="HostCredentialMemberNames" />,
    ///     which contains these and more: a name only this product uses belongs there, where widening refuses
    ///     nothing an add-in author wrote.
    /// </remarks>
    public static FrozenSet<string> CredentialMemberNames { get; } = new[]
        {
            "Secret",
            "ApiKey",
            "Token",
            "ClientSecret",
            "CodeVerifier",
            "RefreshToken",
            "AccessToken",
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The member names credential material is held under in this product's own types.
    /// </summary>
    /// <remarks>
    ///     A superset of <see cref="CredentialMemberNames" />. The three extra names are ones a provider family
    ///     has no reason to carry — a password, a shared key, and a credential a host holds for a runner — so
    ///     stating them here refuses nothing an add-in author wrote while still refusing them on this side.
    /// </remarks>
    public static FrozenSet<string> HostCredentialMemberNames { get; } = CredentialMemberNames
        .Concat(["Password", "Credential", "SharedKey"])
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Renders a secret as its presence only.</summary>
    /// <param name="secret">The secret material, which is never included in the result.</param>
    public static string Elide(string? secret)
    {
        return string.IsNullOrEmpty(secret) ? "none" : "[redacted]";
    }

    /// <summary>
    ///     Renders a header or query-parameter collection as its key names only. The values are elided because an
    ///     operator is free to put a credential in either: an <c>Authorization</c> header and an <c>?api-key=</c>
    ///     query parameter are how several providers expect one.
    /// </summary>
    /// <remarks>
    ///     The key names are written by an operator or a provider family, so a control character in one would
    ///     otherwise reach a log verbatim. A line break in a key would end the log line and let the rest of the
    ///     key be read as a further line, so control characters are replaced before the names are joined.
    /// </remarks>
    /// <param name="values">The collection whose keys to render; values are never included.</param>
    public static string KeyNames(IReadOnlyDictionary<string, string>? values)
    {
        return values is null || values.Count == 0
            ? "none"
            : string.Join(", ", values.Keys.Select(WithoutControlCharacters));
    }

    /// <summary>
    ///     Renders an address as its scheme, host and port. Userinfo, path, query and fragment are dropped,
    ///     because each of them carries a credential in some provider's expected form and the value an operator
    ///     typed into an address field is not judged safe to print.
    /// </summary>
    /// <param name="url">The address, whose credential-bearing parts are never included in the result.</param>
    public static string Address(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "none";
        }

        // An address that does not parse cannot be reduced to its safe parts, and printing it whole is the thing
        // this helper exists to avoid, so it renders as unparsable. Validation refuses such an address before it
        // reaches a provider, so what arrives here is the rejected input.
        //
        // The path is dropped with the rest. A provider that carries a key in it, and a gateway that routes on a
        // tenant segment, both put credential material there, and nothing here can tell which segment is which.
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : "[unparsable]";
    }

    /// <summary>
    ///     Renders an identifier a caller supplied, such as a provider family key or an authentication mode, with
    ///     its control characters replaced.
    /// </summary>
    /// <remarks>
    ///     These values reach a log line before anything has refused them: a request naming a family this build
    ///     does not have, or an authentication mode the family does not declare, is rendered by the log before the
    ///     check that turns it away. A line break in one ends the log line and lets the rest be read as a further
    ///     line, which would let an administrator write entries into the installation's own log.
    /// </remarks>
    /// <param name="value">The identifier as the caller supplied it.</param>
    public static string Identifier(string? value)
    {
        return string.IsNullOrEmpty(value) ? "none" : WithoutControlCharacters(value);
    }

    // Replaced and not dropped, so a name that was nothing but control characters still renders as something an
    // operator can see was present.
    private static string WithoutControlCharacters(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return key.Any(char.IsControl)
            ? string.Create(
                key.Length, key, (span, source) =>
                {
                    for (var index = 0; index < source.Length; index++)
                    {
                        span[index] = char.IsControl(source[index]) ? '�' : source[index];
                    }
                })
            : key;
    }
}
