// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace MeisterDev.ProPR.Hosting;

/// <summary>
///     Reads the networks that ProPR accepts X-Forwarded-For and X-Forwarded-Proto from in addition to loopback and the
///     private IPv4 ranges. A proxy that connects from another range, such as the ingress of an Azure Container Apps
///     environment with workload profiles, has to be listed before ProPR uses the scheme that the proxy reports.
/// </summary>
internal static class TrustedProxyNetworks
{
    public const string ConfigurationKey = "MEISTER_TRUSTED_PROXY_NETWORKS";

    /// <summary>Returns the networks of a comma-separated list in CIDR notation, or none for an unset or blank value.</summary>
    /// <exception cref="OptionsValidationException">
    ///     An entry is not a network in CIDR notation, or it covers every address. The host logs the exception as a fatal
    ///     error and ends the start with exit code 1.
    /// </exception>
    public static IReadOnlyList<IPNetwork> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var networks = new List<IPNetwork>();
        foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseNetwork(entry, out var network))
            {
                throw Refuse(
                    $"{ConfigurationKey} contains '{entry}', which is not a network address followed by its prefix length, "
                    + "such as 100.100.0.0/17. Write a single address with the prefix length /32 for IPv4 or /128 for IPv6.");
            }

            // A prefix length of 0 covers every address, and every client could then set the scheme and the client address.
            if (network.PrefixLength == 0)
            {
                throw Refuse($"{ConfigurationKey} contains '{entry}', which covers every address. List the networks of your proxies.");
            }

            networks.Add(network);
        }

        return networks;
    }

    // IPNetwork.TryParse clears the bits of the address after the prefix, so it reads 10.0.0.5/8 as 10.0.0.0/8. An entry
    // whose address is not the network address is refused, because a mistyped entry would otherwise trust a network that
    // the operator did not mean.
    private static bool TryParseNetwork(string entry, out IPNetwork network)
    {
        network = default;
        var separator = entry.LastIndexOf('/');
        return separator > 0
               && IPAddress.TryParse(entry.AsSpan(0, separator), out var address)
               && IPNetwork.TryParse(entry, out network)
               && address.Equals(network.BaseAddress);
    }

    private static OptionsValidationException Refuse(string failure)
    {
        return new OptionsValidationException(Options.DefaultName, typeof(ForwardedHeadersOptions), [failure]);
    }
}
