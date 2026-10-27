// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     Refuses every request of the HTTP client that reaches identity providers when it does not use https. A token
///     request carries the client secret, the authorization code and the PKCE code verifier, so it must not leave
///     ProPR in cleartext. Development accepts plain http, for a provider on the developer's machine.
/// </summary>
/// <param name="allowPlainHttp">Whether plain http is accepted, which the host allows in Development only.</param>
/// <param name="logger">Logger for refused requests.</param>
public sealed class ExternalSignInTransportHandler(bool allowPlainHttp, ILogger<ExternalSignInTransportHandler> logger)
    : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scheme = request.RequestUri?.Scheme;
        var isAllowed = scheme == Uri.UriSchemeHttps || (allowPlainHttp && scheme == Uri.UriSchemeHttp);
        if (isAllowed)
        {
            return base.SendAsync(request, cancellationToken);
        }

        var address = SecretSafeRendering.Address(request.RequestUri?.AbsoluteUri);
        logger.LogWarning("ExternalSignInRequestRefused Address={Address} Reason={Reason}", address, "https_required");
        throw new HttpRequestException($"ProPR sends requests to identity providers over https only, and {address} does not use https.");
    }
}
