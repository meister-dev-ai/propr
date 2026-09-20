// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     Matches a host against one entry of a host list, in the form a tenant's endpoint allow-list and a provider
///     family's declared reach are both written in.
/// </summary>
/// <remarks>
///     <para>
///         An entry is a bare host such as <c>api.openai.com</c>, which matches that host and no other, or a
///         leading-dot suffix such as <c>.openai.azure.com</c>, which matches that host and every subdomain of
///         it. The suffix form is what lets a list name a vendor whose customers each get a hostname of their
///         own.
///     </para>
///     <para>
///         One function, so the two places that read a host list answer the same way: the tenant policy deciding
///         where a connection's traffic may go, and the check on an address a family asks the host to open in an
///         operator's browser. Two implementations would drift, and the second one would be the one nobody
///         noticed had drifted.
///     </para>
/// </remarks>
public static class ProviderHostPattern
{
    /// <summary>Whether <paramref name="pattern" /> covers <paramref name="host" />.</summary>
    /// <param name="pattern">The list entry, as stored or as declared.</param>
    /// <param name="host">The host component of the address being checked.</param>
    public static bool DoesPatternMatchHost(string? pattern, string? host)
    {
        var entry = Normalize(pattern);
        var subject = Normalize(host);

        if (entry.Length == 0 || subject.Length == 0)
        {
            return false;
        }

        return entry.StartsWith('.')
            ? subject.EndsWith(entry, StringComparison.Ordinal) || subject == entry.TrimStart('.')
            : subject == entry;
    }

    /// <summary>Whether any entry of <paramref name="patterns" /> covers <paramref name="host" />.</summary>
    /// <param name="patterns">The list entries.</param>
    /// <param name="host">The host component of the address being checked.</param>
    public static bool DoesAnyPatternMatchHost(IEnumerable<string>? patterns, string? host)
    {
        return patterns is not null && patterns.Any(pattern => DoesPatternMatchHost(pattern, host));
    }

    /// <summary>
    ///     Whether <paramref name="entry" /> covers every host <paramref name="declaredPattern" /> admits.
    /// </summary>
    /// <param name="entry">The list entry, as stored.</param>
    /// <param name="declaredPattern">The pattern a provider family declared it reaches.</param>
    /// <remarks>
    ///     <para>
    ///         Containment, not overlap. The entry <c>api.example.com</c> does not cover the pattern
    ///         <c>.example.com</c>, because that pattern admits hosts the entry does not name; the entry
    ///         <c>.example.com</c> does cover the pattern <c>api.example.com</c>. Getting the direction wrong
    ///         turns a restriction into a permission.
    ///     </para>
    ///     <para>
    ///         Answered by <see cref="DoesPatternMatchHost" />, because containment reduces to the same
    ///         comparison: a bare pattern is the single host it names, and a suffix pattern is contained exactly
    ///         when it ends with the entry's suffix, which is also when it would match as a host. Naming the
    ///         question separately keeps the direction readable at the call site without adding a matcher that
    ///         could answer differently.
    ///     </para>
    /// </remarks>
    public static bool DoesEntryCoverPattern(string? entry, string? declaredPattern)
    {
        return DoesPatternMatchHost(entry, declaredPattern);
    }

    /// <summary>Whether any entry of <paramref name="entries" /> covers <paramref name="declaredPattern" />.</summary>
    /// <param name="entries">The list entries.</param>
    /// <param name="declaredPattern">The pattern a provider family declared it reaches.</param>
    public static bool DoesAnyEntryCoverPattern(IEnumerable<string>? entries, string? declaredPattern)
    {
        return DoesAnyPatternMatchHost(entries, declaredPattern);
    }

    /// <summary>Puts an entry or a host into the single form both sides of the comparison are read in.</summary>
    /// <param name="value">The entry or host as written.</param>
    public static string Normalize(string? value)
    {
        // The trailing dot of an absolute name is dropped, as the address policy drops it: 'api.openai.com.' and
        // 'api.openai.com' are the same host, and a list entry written without it would otherwise cover only one
        // of the two spellings. A leading dot is the list's own wildcard and stays.
        return value is null ? string.Empty : value.Trim().Trim('/').TrimEnd('.').ToLowerInvariant();
    }
}
