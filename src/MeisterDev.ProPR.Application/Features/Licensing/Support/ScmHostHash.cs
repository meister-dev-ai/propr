// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeisterDev.ProPR.Application.Features.Licensing.Support;

/// <summary>
///     Turns the SCM host base URLs an installation is configured against into salted hashes.
///     <para>
///         The hashes exist so that two reports can be compared for whether they come from the same estate. The
///         plain host is not stored. The salt is the license identifier: installations of one license produce
///         comparable hashes for the same host, and installations of different licenses produce different values.
///         The identifier is disclosed with commercial reports, so it is a namespace salt rather than a secret
///         against a party testing a candidate host.
///     </para>
///     <para>
///         Without a license identifier there is no salt, and the component is recorded as absent rather than
///         being hashed with a fixed one.
///     </para>
///     <para>
///         The salt follows from that choice: the values are comparable only among installations carrying the
///         same license identifier. Activating a license, renewing onto one with a new identifier, and removing
///         one all change the key, so every host hash is recomputed and the profile records one change at that
///         point on every installation of that licensee, with the same hosts configured as before.
///     </para>
/// </summary>
internal static class ScmHostHash
{
    /// <summary>Domain-separates the key from any other use of the license identifier.</summary>
    private const string SaltPrefix = "propr:host-hash:";

    /// <summary>
    ///     The one place a configured host URL becomes the value that is hashed: the scheme and the host in
    ///     lower case, plus the port when it is not the scheme's default.
    ///     <para>
    ///         Everything else a connection URL carries — path, query, credentials, trailing slash — is dropped,
    ///         so two connections to the same host under different organization paths count as one host. A value
    ///         that is not an absolute URL has no host to normalize and is left out.
    ///     </para>
    /// </summary>
    /// <param name="hostBaseUrl">The configured connection URL.</param>
    /// <returns>The normalized host, or <see langword="null" /> when the value carries none.</returns>
    internal static string? Normalize(string? hostBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(hostBaseUrl) ||
            !Uri.TryCreate(hostBaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.DnsSafeHost.ToLowerInvariant();

        // A literal IPv6 address is written in brackets so the port separator stays unambiguous.
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            host = $"[{host}]";
        }

        return uri.IsDefaultPort
            ? $"{scheme}://{host}"
            : $"{scheme}://{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    ///     Hashes every host that can be normalized, as a set: one entry per distinct host, in ordinal order.
    /// </summary>
    /// <param name="hostBaseUrls">The configured connection URLs.</param>
    /// <param name="licenseId">The identifier of the verified license the salt is derived from.</param>
    /// <returns>The sorted hash set, in lower-case hexadecimal.</returns>
    internal static IReadOnlyList<string> ComputeSet(IEnumerable<string?> hostBaseUrls, string licenseId)
    {
        ArgumentNullException.ThrowIfNull(hostBaseUrls);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseId);

        var normalizedHosts = hostBaseUrls
            .Select(Normalize)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var hmac = new HMACSHA256(SHA256.HashData(Encoding.UTF8.GetBytes(SaltPrefix + licenseId)));

        var hashes = normalizedHosts
            .Select(host => Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(host))))
            .ToList();

        hashes.Sort(StringComparer.Ordinal);

        return hashes;
    }
}
