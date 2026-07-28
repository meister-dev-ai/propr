// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Authenticates Google requests from what the profile stored: an API key for the Gemini API, a
///     service-account credential for Vertex.
/// </summary>
/// <remarks>
///     <para>
///         Vertex tokens are minted by Google's own auth library rather than by hand. Signing a JWT and
///         exchanging it is a solved problem with a short-lived result, and getting the refresh timing wrong
///         produces intermittent 401s that look like a permissions change.
///     </para>
///     <para>
///         Credentials are kept per stored secret so the library's own token cache survives between calls —
///         without it every request would pay a token exchange. They are keyed by a hash of the secret rather
///         than by the secret, so a dictionary dump cannot hand out credentials.
///     </para>
/// </remarks>
public sealed class GoogleCredentialSource : IGoogleCredentialSource
{
    /// <summary>The header the Gemini API reads an API key from.</summary>
    public const string ApiKeyHeaderName = "x-goog-api-key";

    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly ConcurrentDictionary<string, GoogleCredential> _credentials = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task AuthenticateAsync(
        HttpRequestMessage request,
        ProviderEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(endpoint.Secret))
        {
            throw new InvalidOperationException(
                "A Google connection needs a credential: an API key for the Gemini API, or the JSON key of a "
                + "service account for Vertex AI.");
        }

        if (!GoogleEndpointResolution.IsVertex(endpoint.BaseUrl))
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, endpoint.Secret);
            return;
        }

        var credential = this._credentials.GetOrAdd(Fingerprint(endpoint.Secret), _ => FromJson(endpoint.Secret));
        var token = await credential.UnderlyingCredential
            .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static GoogleCredential FromJson(string secret)
    {
        try
        {
            // CredentialFactory rather than GoogleCredential.FromJson, which Google deprecated over a security
            // risk in sniffing the kind out of an arbitrary payload. Its replacement wants the kind named, and a
            // tenant may store a service-account key or a workload-identity configuration, so the kind is read
            // from the payload's own "type" field and passed in rather than assumed.
            return CredentialFactory
                .FromJson(secret, ReadCredentialType(secret))
                .CreateScoped(CloudPlatformScope);
        }
        catch (Exception failure) when (failure is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        {
            // The likely mistake is pasting an API key into a Vertex profile, and the library's own message
            // does not say that.
            throw new InvalidOperationException(
                "Vertex AI authenticates with a Google credential. Store the JSON key of a service account "
                + "that may call the Vertex AI API.",
                failure);
        }
    }

    /// <summary>
    ///     Reads the credential kind the payload declares. A payload with no <c>type</c> is treated as a service
    ///     account, which is both the common case and what the failure message tells an operator to store.
    /// </summary>
    /// <param name="secret">The stored credential JSON.</param>
    /// <returns>The credential type to build.</returns>
    private static string ReadCredentialType(string secret)
    {
        using var document = JsonDocument.Parse(secret);

        return document.RootElement.ValueKind == JsonValueKind.Object
               && document.RootElement.TryGetProperty("type", out var declared)
               && declared.ValueKind == JsonValueKind.String
               && declared.GetString() is { Length: > 0 } kind
            ? kind
            : JsonCredentialParameters.ServiceAccountCredentialType;
    }

    private static string Fingerprint(string secret)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
