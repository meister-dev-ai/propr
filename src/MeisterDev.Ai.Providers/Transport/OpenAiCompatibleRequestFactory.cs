// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Builds HTTP requests for OpenAI-compatible admin operations.
/// </summary>
public sealed class OpenAiCompatibleRequestFactory
{
    public HttpRequestMessage CreateModelsRequest(ProviderEndpoint endpoint)
    {
        var uri = BuildRelativeUri(endpoint.BaseUrl, "models", endpoint.DefaultQueryParams);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (!string.IsNullOrWhiteSpace(endpoint.Secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret);
        }

        foreach (var header in endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
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
