// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Diagnostics;

/// <summary>
///     Helpers for rendering credential-bearing types without their credentials.
/// </summary>
/// <remarks>
///     A record's generated <c>ToString</c> prints every property, so any type holding a secret leaks it the first
///     time someone interpolates it into a message or a log line. That is not a hypothetical: it needs no logging
///     configuration to go wrong and no code review to notice, because the call site looks completely ordinary.
///     Types that hold secrets therefore override <c>ToString</c> using these helpers, which makes the safe
///     rendering the default rather than something each call site has to remember.
/// </remarks>
public static class SecretSafeRendering
{
    /// <summary>Renders a secret as its presence only.</summary>
    /// <param name="secret">The secret material, which is never included in the result.</param>
    public static string Elide(string? secret)
    {
        return string.IsNullOrEmpty(secret) ? "none" : "[redacted]";
    }

    /// <summary>
    ///     Renders a header or query-parameter collection as its key names only. The values are elided because an
    ///     operator is free to put a credential in either — an <c>Authorization</c> header or an <c>?api-key=</c>
    ///     query parameter is exactly how several providers expect one.
    /// </summary>
    /// <param name="values">The collection whose keys to render; values are never included.</param>
    public static string KeyNames(IReadOnlyDictionary<string, string>? values)
    {
        return values is null || values.Count == 0 ? "none" : string.Join(", ", values.Keys);
    }
}
