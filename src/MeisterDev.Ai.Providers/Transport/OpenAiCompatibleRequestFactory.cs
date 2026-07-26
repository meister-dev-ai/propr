// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Builds HTTP requests for provider admin operations against a <c>/models</c> endpoint returning
///     <c>{ data: [ { id } ] }</c> — the shape OpenAI defined and that Anthropic and the compatible long tail
///     also serve.
/// </summary>
public sealed class OpenAiCompatibleRequestFactory
{
    /// <summary>The header name a provider using <see cref="AiAuthMode.XApiKey" /> expects its key in.</summary>
    public const string ApiKeyHeaderName = "x-api-key";

    public HttpRequestMessage CreateModelsRequest(ProviderEndpoint endpoint)
    {
        var uri = BuildRelativeUri(endpoint.BaseUrl, "models", endpoint.DefaultQueryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        ApplyCredential(request, endpoint);

        foreach (var header in endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    /// <summary>
    ///     Puts the credential where the auth mode says it goes. Anthropic rejects a bearer token and reads
    ///     <c>x-api-key</c> instead, which is a wire-level difference rather than a naming preference, so the
    ///     mode — not the provider family — decides.
    /// </summary>
    /// <param name="request">The request to authenticate.</param>
    /// <param name="endpoint">The endpoint carrying the credential and its mode.</param>
    public static void ApplyCredential(HttpRequestMessage request, ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(endpoint.Secret))
        {
            return;
        }

        if (endpoint.AuthMode == AiAuthMode.XApiKey)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, endpoint.Secret);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret);
    }

    private static Uri BuildRelativeUri(
        string baseUrl,
        string relativePath,
        IReadOnlyDictionary<string, string>? queryParams)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = $"{path}/{relativePath.TrimStart('/')}";

        if (queryParams is not null && queryParams.Count > 0)
        {
            builder.Query = string.Join(
                "&",
                queryParams.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        }

        return builder.Uri;
    }
}
