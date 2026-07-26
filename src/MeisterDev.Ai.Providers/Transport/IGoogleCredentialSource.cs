// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Authenticates a request to one of Google's generateContent surfaces.
/// </summary>
/// <remarks>
///     A seam because the two surfaces authenticate differently and one of them mints tokens: the Gemini API
///     takes a key header, while Vertex takes a bearer token signed from a service-account credential and
///     refreshed on a clock. Behind an interface, neither the client nor its tests need a Google account.
/// </remarks>
public interface IGoogleCredentialSource
{
    /// <summary>Applies the endpoint's credential to a request.</summary>
    /// <param name="request">The request to authenticate.</param>
    /// <param name="endpoint">The endpoint carrying the credential.</param>
    /// <param name="cancellationToken">Cancels the token exchange, where one is needed.</param>
    /// <exception cref="InvalidOperationException">The stored credential cannot be used for this surface.</exception>
    Task AuthenticateAsync(HttpRequestMessage request, ProviderEndpoint endpoint, CancellationToken cancellationToken = default);
}
