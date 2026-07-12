// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Features.ProCursor.Broker.Auth;

/// <summary>
///     Validates the shared key used by ProCursor service-to-service requests.
/// </summary>
public sealed class ProCursorSharedKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ProCursorRemoteOptions> remoteOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = remoteOptions.Value.SharedKey;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("ProCursor shared key is not configured."));
        }

        if (!this.Request.Headers.TryGetValue(ProCursorSharedKeyAuthenticationDefaults.HeaderName, out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing ProCursor shared key."));
        }

        // Constant-time comparison so a timing side-channel cannot be used to recover the shared key
        // (mirrors the webhook verifiers). FixedTimeEquals also handles the length mismatch internally.
        var providedKeyBytes = Encoding.UTF8.GetBytes(headerValues.FirstOrDefault() ?? string.Empty);
        var configuredKeyBytes = Encoding.UTF8.GetBytes(configuredKey);
        if (!CryptographicOperations.FixedTimeEquals(providedKeyBytes, configuredKeyBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid ProCursor shared key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "procursor-service")],
            ProCursorSharedKeyAuthenticationDefaults.Scheme);
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    ProCursorSharedKeyAuthenticationDefaults.Scheme)));
    }
}
