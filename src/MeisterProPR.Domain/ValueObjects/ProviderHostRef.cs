// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Normalized provider-family plus host authority reference.</summary>
public sealed record ProviderHostRef
{
    /// <summary>Initializes a new instance of the <see cref="ProviderHostRef" /> class.</summary>
    /// <param name="provider">The SCM provider.</param>
    /// <param name="hostBaseUrl">The base URL of the host authority.</param>
    /// <exception cref="ArgumentException">Thrown when hostBaseUrl is null, whitespace, or not an absolute URL.</exception>
    public ProviderHostRef(ScmProvider provider, string hostBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostBaseUrl);

        if (!Uri.TryCreate(hostBaseUrl.Trim(), UriKind.Absolute, out var parsedUri))
        {
            throw new ArgumentException("HostBaseUrl must be an absolute URL.", nameof(hostBaseUrl));
        }

        this.Provider = provider;
        this.HostBaseUrl = Normalize(parsedUri);
    }

    /// <summary>Gets the SCM provider.</summary>
    public ScmProvider Provider { get; }

    /// <summary>Gets the normalized base URL of the host authority.</summary>
    public string HostBaseUrl { get; }

    /// <summary>Normalizes the given URI to its authority part without path, query, or fragment.</summary>
    /// <param name="uri">The URI to normalize.</param>
    /// <returns>The normalized authority part of the URI.</returns>
    private static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
