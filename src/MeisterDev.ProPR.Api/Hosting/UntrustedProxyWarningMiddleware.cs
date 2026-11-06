// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Hosting;

/// <summary>
///     Writes a warning for the first request that carries X-Forwarded-For or X-Forwarded-Proto from an address outside
///     the trusted proxy networks. The forwarded headers middleware ignores both headers of such a request, and ProPR uses
///     the scheme and the address of the connection. Behind a proxy that terminates TLS, ProPR then treats the requests of
///     the proxy as http requests, refuses the ProPR Lens sign-in and sets its cookies without the Secure attribute. The
///     warning contains the address of the proxy, so that an operator can add its network to the trusted networks.
/// </summary>
/// <remarks>
///     Runs ahead of the forwarded headers middleware. That middleware replaces the connection address of a request from a
///     trusted proxy with the forwarded client address, and a test after it would check the client address.
/// </remarks>
internal sealed partial class UntrustedProxyWarningMiddleware(
    RequestDelegate next,
    IOptions<ForwardedHeadersOptions> options,
    ILogger<UntrustedProxyWarningMiddleware> logger)
{
    private int _warned;

    public Task InvokeAsync(HttpContext context)
    {
        if (Volatile.Read(ref this._warned) == 0
            && context.Connection.RemoteIpAddress is { } address
            && this.CarriesForwardedHeaders(context.Request.Headers)
            && !this.IsTrusted(address)
            && Interlocked.Exchange(ref this._warned, 1) == 0)
        {
            LogUntrustedProxy(logger, Unmapped(address).ToString(), TrustedProxyNetworks.ConfigurationKey);
        }

        return next(context);
    }

    private bool CarriesForwardedHeaders(IHeaderDictionary headers)
    {
        return headers.ContainsKey(options.Value.ForwardedForHeaderName) || headers.ContainsKey(options.Value.ForwardedProtoHeaderName);
    }

    // The same test that the forwarded headers middleware applies. An IPv4 address that arrives mapped to IPv6, as on a
    // dual-stack socket, also matches the IPv4 networks.
    private bool IsTrusted(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6 && this.IsTrusted(address.MapToIPv4()))
        {
            return true;
        }

        return options.Value.KnownProxies.Contains(address) || options.Value.KnownIPNetworks.Any(network => network.Contains(address));
    }

    private static IPAddress Unmapped(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ProPR ignored the X-Forwarded-For and X-Forwarded-Proto headers of a request from {ProxyAddress}, because the "
                  + "address is outside the trusted proxy networks. ProPR uses the scheme of the connection instead. Behind a proxy "
                  + "that terminates TLS, ProPR treats the requests of that proxy as http requests, refuses the ProPR Lens sign-in "
                  + "and sets its cookies without the Secure attribute. If a proxy at this address forwards requests to ProPR, add "
                  + "its network to {Setting}. ProPR writes this warning once per process.")]
    private static partial void LogUntrustedProxy(ILogger logger, string proxyAddress, string setting);
}
