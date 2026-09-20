// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.BedrockAddIn;

/// <summary>
///     Takes the client this family's calls go out on from the host, for one endpoint and one purpose.
/// </summary>
/// <remarks>
///     The factory rides on the endpoint, so every call site reads it the same way and none of them constructs a
///     client of its own. A client the family built itself would not carry the host's connect-time address
///     check, which is the control that defends against an operator-supplied base URL.
/// </remarks>
internal static class BedrockTransport
{
    /// <summary>What a refusal says when the host supplied no way to reach the network.</summary>
    internal static readonly string NoClientFactory = UnreachableClients.Refusal("AWS Bedrock");

    /// <summary>The client factory for an endpoint, or null where the host supplied none.</summary>
    /// <param name="endpoint">The endpoint the call is made against.</param>
    internal static IProviderHttpClientFactory? Factory(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.HostContext?.Http;
    }

    /// <summary>The client for one endpoint and purpose.</summary>
    /// <param name="endpoint">The endpoint the call is made against.</param>
    /// <param name="purpose">What the calls on this client are for.</param>
    /// <exception cref="InvalidOperationException">The host supplied no client factory.</exception>
    internal static HttpClient Client(ProviderEndpoint endpoint, ProviderHttpPurpose purpose)
    {
        return Factory(endpoint) is { } http
            ? http.Create(purpose)
            : throw new InvalidOperationException(NoClientFactory);
    }
}
