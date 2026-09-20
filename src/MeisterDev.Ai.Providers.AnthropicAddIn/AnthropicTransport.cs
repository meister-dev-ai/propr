// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.AnthropicAddIn;

/// <summary>
///     Takes the client this family's calls go out on from the host, for one endpoint and one purpose.
/// </summary>
/// <remarks>
///     The factory rides on the endpoint, so every call site reads it the same way and none of them constructs a
///     client of its own. A client the family built itself would not carry the host's connect-time address
///     check, which is the control that defends against an operator-supplied base URL.
/// </remarks>
internal static class AnthropicTransport
{
    /// <summary>What a refusal says when the host supplied no way to reach the network.</summary>
    internal static readonly string NoClientFactory = UnreachableClients.Refusal("Anthropic");

    /// <summary>The client for one endpoint and purpose.</summary>
    /// <param name="endpoint">The endpoint the call is made against.</param>
    /// <param name="purpose">What the calls on this client are for.</param>
    internal static HttpClient Client(ProviderEndpoint endpoint, ProviderHttpPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return Client(endpoint.HostContext?.Http, purpose);
    }

    /// <summary>The client for one purpose, from a factory a caller already holds.</summary>
    /// <param name="http">The host's client factory, or null where the host supplied none.</param>
    /// <param name="purpose">What the calls on this client are for.</param>
    /// <exception cref="InvalidOperationException">The host supplied no client factory.</exception>
    internal static HttpClient Client(IProviderHttpClientFactory? http, ProviderHttpPurpose purpose)
    {
        return http is null ? throw new InvalidOperationException(NoClientFactory) : http.Create(purpose);
    }
}
