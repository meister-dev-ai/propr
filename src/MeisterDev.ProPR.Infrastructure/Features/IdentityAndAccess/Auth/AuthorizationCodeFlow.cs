// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     The parts of the OAuth 2.0 authorization code flow that every sign-in through an external identity provider
///     shares: the URL the browser is sent to, the random values of one sign-in, the PKCE code challenge, and the
///     redemption of the authorization code at the token endpoint.
/// </summary>
public static class AuthorizationCodeFlow
{
    /// <summary>The name of the HTTP client that reaches identity providers.</summary>
    public const string HttpClientName = "ExternalSignIn";

    /// <summary>
    ///     The largest answer of an identity provider that the HTTP client reads, 1 MiB. A token response holds a few
    ///     tokens of some kilobytes each, and a discovery document or a key set is smaller, so a larger answer is
    ///     refused before it fills memory.
    /// </summary>
    public const int MaxResponseContentBytes = 1024 * 1024;

    /// <summary>Returns <paramref name="endpoint" /> with <paramref name="parameters" /> appended to its query.</summary>
    /// <param name="endpoint">The authorization endpoint of the identity provider.</param>
    /// <param name="parameters">The parameters of the authorization request.</param>
    public static Uri BuildAuthorizationUri(Uri endpoint, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var builder = new UriBuilder(endpoint);
        var existingQuery = builder.Query;
        var query = string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        builder.Query = string.IsNullOrWhiteSpace(existingQuery)
            ? query
            : $"{existingQuery.TrimStart('?')}&{query}";
        return builder.Uri;
    }

    /// <summary>
    ///     Returns 32 random bytes as base64url text without padding. A sign-in uses such values for the state, the
    ///     nonce and the PKCE code verifier. RFC 7636 requires a code verifier of 43 to 128 characters of that
    ///     alphabet, and 32 bytes give 43.
    /// </summary>
    public static string CreateRandomValue()
    {
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Returns the S256 code challenge of RFC 7636 for <paramref name="codeVerifier" />.</summary>
    /// <param name="codeVerifier">The code verifier that the token request sends later.</param>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>Posts <paramref name="form" /> to the token endpoint and parses the JSON answer.</summary>
    /// <param name="httpClient">The client that reaches the identity provider.</param>
    /// <param name="tokenEndpoint">The token endpoint of the identity provider.</param>
    /// <param name="form">The form fields of the token request, the authorization code among them.</param>
    /// <param name="userAgent">A user agent the provider requires, or <see langword="null" /> for none.</param>
    /// <param name="logger">Logger for a request that fails before an answer arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The status code of the answer, and its JSON body. The body is <see langword="null" /> when the status code
    ///     is not a success or the answer is not JSON. Both are <see langword="null" /> when the request fails without
    ///     an answer, for example because the client refuses the transport or the answer exceeds its buffer limit. The
    ///     caller disposes the document.
    /// </returns>
    public static async Task<(HttpStatusCode? StatusCode, JsonDocument? Document)> RedeemCodeAsync(
        HttpClient httpClient,
        Uri tokenEndpoint,
        IEnumerable<KeyValuePair<string, string>> form,
        string? userAgent,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (userAgent is not null)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "ExternalSignInTokenRequestFailed TokenEndpoint={TokenEndpoint}",
                SecretSafeRendering.Address(tokenEndpoint.AbsoluteUri));
            return (null, null);
        }

        using var answer = response;
        if (!answer.IsSuccessStatusCode)
        {
            return (answer.StatusCode, null);
        }

        var content = await answer.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return (answer.StatusCode, JsonDocument.Parse(content));
        }
        catch (JsonException)
        {
            return (answer.StatusCode, null);
        }
    }
}
