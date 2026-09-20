// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     What an address a provider family asks the host to open in an operator's browser has to be.
/// </summary>
/// <remarks>
///     <para>
///         The address is authored by the family, and the console it reaches is the administration origin. A
///         <c>javascript:</c> address handed to the browser runs script there, so the scheme is checked; and a
///         family whose declaration names two vendor hosts could otherwise send the administrator anywhere it
///         liked over https, so the host is checked against the same declaration that bounds where the family's
///         own traffic goes. This is the second reader of those patterns: the first says where a family's code
///         reaches, this one says where it may send a person.
///     </para>
///     <para>
///         The loopback carve-out is the same one a declared address field gets, and exists for the same
///         address: a family that declared it needs the host process and the operator's browser on one machine
///         completes its flow at <c>http://127.0.0.1:&lt;port&gt;</c>, which no declared pattern names and which is
///         not egress. Every other address carries both checks.
///     </para>
///     <para>
///         The installation's egress settings are not consulted. They govern where the host process sends
///         traffic; this governs where an operator's own browser is sent, which the host does not reach.
///     </para>
/// </remarks>
public static class ProviderBrowserUrlPolicy
{
    /// <summary>
    ///     Why the host refuses to open <paramref name="url" /> for <paramref name="declaration" />, or
    ///     <see langword="null" /> when it permits it.
    /// </summary>
    /// <param name="declaration">The family that returned the address, carrying what it reaches and whether it declared co-location.</param>
    /// <param name="url">The address as the family returned it.</param>
    /// <param name="listenerPorts">
    ///     The loopback ports this connection's declared values say the family listens on, from
    ///     <see cref="ProviderDeclaration.ListenerPortsIn" />. The loopback carve-out is held to these when there
    ///     are any. Empty means the family told the host nothing about its port, and the carve-out then reaches
    ///     any loopback port, as it did before a family could name its port fields.
    /// </param>
    public static string? GetRefusalReason(
        ProviderDeclaration declaration,
        string? url,
        IReadOnlyCollection<int>? listenerPorts = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "The address to open is not an absolute URL, so it names no scheme and no host. An action "
                   + "opens an absolute https address on a host its family declared.";
        }

        // Userinfo is refused before the host is looked at, because the host patterns are what the administrator
        // is told the address was checked against and userinfo changes which host a browser actually reaches:
        // 'https://evil@api.example.com' passes a pattern covering api.example.com, and a browser can present it
        // as a credential to that host or, with a further '@', treat the covered name as a user name.
        if (ProbeTargetChecks.CarriesUserInfo(uri))
        {
            return "The address to open carries a credential in the address itself, which is refused because it "
                   + "makes the host the browser reaches something other than the host the address names.";
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (isHttp && uri.IsLoopback)
        {
            // Not egress, and no declared pattern names it: the address is this machine, reached by the
            // operator's own browser on the way back from the vendor.
            if (!declaration.RequiresBrowserCoLocation)
            {
                return $"The address to open uses the '{uri.Scheme}' scheme on the loopback host '{uri.Host}', "
                       + "which is opened only for a family that declared it needs the host and the operator's "
                       + $"browser on one machine. The family '{declaration.Key}' declared no such requirement.";
            }

            // Held to the port the family listens on where the family named the field that holds it. The
            // operator's browser carries whatever session it has for a local service, which the family's own
            // code does not, so an address on some other local port reaches something this family never binds.
            return listenerPorts is not { Count: > 0 } || listenerPorts.Contains(uri.Port)
                ? null
                : $"The address to open is on the loopback port {uri.Port.ToString(CultureInfo.InvariantCulture)}"
                  + $", and the family '{declaration.Key}' listens on "
                  + string.Join(" or ", listenerPorts.Select(port => port.ToString(CultureInfo.InvariantCulture)))
                  + ". A loopback address is opened on the port the connection says the family binds.";
        }

        if (!isHttps)
        {
            return $"The address to open uses the '{uri.Scheme}' scheme. An action opens an https address, or "
                   + "plain http to a loopback host where its family declared co-location.";
        }

        return ProviderHostPattern.DoesAnyPatternMatchHost(declaration.ReachedHostPatterns, uri.Host)
            ? null
            : $"The address to open is on the host '{uri.Host}', which none of the patterns the family "
              + $"'{declaration.Key}' declared covers"
              + (declaration.ReachedHostPatterns.Count == 0
                  ? ", since it declared none."
                  : $" (declared: {string.Join(", ", declaration.ReachedHostPatterns)}).");
    }
}
