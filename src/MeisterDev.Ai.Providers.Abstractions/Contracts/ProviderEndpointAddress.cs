// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Builds a request address from an endpoint's base URL.
/// </summary>
/// <remarks>
///     A base URL may already carry query parameters. A gateway in front of a provider uses them to name a
///     tenant or a route, and the operator entered them as part of the address. Assigning the endpoint's
///     declared defaults over the whole query drops those. Every family needs the same merge, so it lives here
///     instead of in each one.
/// </remarks>
public static class ProviderEndpointAddress
{
    /// <summary>
    ///     Appends <paramref name="pathSuffix" /> to the endpoint's path and merges the endpoint's default query
    ///     parameters with any the base URL already carries. A default replaces a base-URL parameter of the same
    ///     name, because the default is the one the family asked for.
    /// </summary>
    /// <param name="endpoint">The endpoint to address.</param>
    /// <param name="pathSuffix">The path segment to append, without a leading separator.</param>
    public static Uri For(ProviderEndpoint endpoint, string pathSuffix)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathSuffix);

        var builder = new UriBuilder(new Uri(endpoint.BaseUrl, UriKind.Absolute));
        builder.Path = $"{builder.Path.TrimEnd('/')}/{pathSuffix.TrimStart('/')}";
        builder.Query = MergeQuery(builder.Query, endpoint.DefaultQueryParams);

        return builder.Uri;
    }

    /// <summary>
    ///     Merges the endpoint's default query parameters onto an address a client library already composed.
    /// </summary>
    /// <remarks>
    ///     <see cref="For" /> builds an address from the base URL, which a family does for a call it composes
    ///     itself. A client library composes its own, from the endpoint it was handed, and the defaults have to
    ///     reach that one too: a provider that carries its key as a query parameter is configured this way, and
    ///     a runtime call without it is unauthenticated.
    /// </remarks>
    /// <param name="address">The address the caller composed.</param>
    /// <param name="defaults">The endpoint's default query parameters, or null for none.</param>
    public static Uri WithDefaultQuery(Uri address, IReadOnlyDictionary<string, string>? defaults)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (defaults is not { Count: > 0 })
        {
            return address;
        }

        var builder = new UriBuilder(address) { Query = MergeQuery(address.Query, defaults) };

        return builder.Uri;
    }

    private static string MergeQuery(string? existing, IReadOnlyDictionary<string, string>? defaults)
    {
        var merged = new List<KeyValuePair<string, string>>();
        var overridden = defaults is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(defaults.Keys, StringComparer.Ordinal);

        foreach (var pair in (existing ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];

            // Kept encoded as the operator entered it. Decoding and re-encoding would rewrite a value the
            // provider may compare byte for byte.
            if (!overridden.Contains(Uri.UnescapeDataString(name)))
            {
                merged.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        if (defaults is not null)
        {
            foreach (var (name, value) in defaults)
            {
                merged.Add(new KeyValuePair<string, string>(Uri.EscapeDataString(name), Uri.EscapeDataString(value)));
            }
        }

        if (merged.Count == 0)
        {
            return string.Empty;
        }

        var query = new StringBuilder();
        foreach (var (name, value) in merged)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(name).Append('=').Append(value);
        }

        return query.ToString();
    }
}
