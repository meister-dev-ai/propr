// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Works out which of Google's two generateContent surfaces an endpoint is, and where a model lives on it.
/// </summary>
/// <remarks>
///     The protocol is the same on both, but everything around it differs: the Gemini API authenticates with an
///     API key and addresses models globally, while Vertex authenticates with a Google credential and addresses
///     them inside one project and location. Which one an endpoint is, is decided by its host rather than by an
///     extra setting an operator could set inconsistently with the URL.
/// </remarks>
public static class GoogleEndpointResolution
{
    /// <summary>The query parameter naming the GCP project a Vertex endpoint serves.</summary>
    public const string ProjectParameterName = "project";

    /// <summary>The host suffix that identifies the Vertex surface.</summary>
    public const string VertexHostSuffix = "aiplatform.googleapis.com";

    /// <summary>Reports whether an endpoint is the Vertex surface rather than the Gemini API.</summary>
    /// <param name="baseUrl">The configured base URL.</param>
    public static bool IsVertex(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
               && uri.Host.EndsWith(VertexHostSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reads the location a Vertex host serves — <c>europe-west4-aiplatform.googleapis.com</c> serves
    ///     <c>europe-west4</c> — or <see langword="null" /> for the global host, which names none.
    /// </summary>
    /// <param name="host">The host to read.</param>
    public static string? LocationFromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || !host.EndsWith(VertexHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prefix = host[..^VertexHostSuffix.Length].TrimEnd('.', '-');
        return prefix.Length == 0 ? null : prefix.ToLowerInvariant();
    }

    /// <summary>Reads the GCP project a Vertex endpoint serves, or <see langword="null" /> when it names none.</summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    public static string? ResolveProject(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.DefaultQueryParams is { } parameters
               && parameters.TryGetValue(ProjectParameterName, out var project)
               && !string.IsNullOrWhiteSpace(project)
            ? project.Trim()
            : null;
    }

    /// <summary>Builds the URI for one method against one model.</summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    /// <param name="remoteModelId">The model to address.</param>
    /// <param name="method">The generateContent-family method, without its colon.</param>
    /// <exception cref="InvalidOperationException">A Vertex endpoint names no project or location.</exception>
    public static Uri BuildModelUri(ProviderEndpoint endpoint, string remoteModelId, string method)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteModelId);

        var baseUri = new Uri(endpoint.BaseUrl, UriKind.Absolute);
        var builder = new UriBuilder(baseUri);
        var basePath = builder.Path.TrimEnd('/');

        // A model id may already be written in the provider's own qualified form, in which case repeating the
        // prefix would address a model called "models/models/…".
        var modelPath = remoteModelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                        || remoteModelId.StartsWith("publishers/", StringComparison.OrdinalIgnoreCase)
            ? remoteModelId
            : $"models/{remoteModelId}";

        if (!IsVertex(endpoint.BaseUrl))
        {
            builder.Path = $"{EnsureVersion(basePath, "v1beta")}/{modelPath}:{method}";
            return builder.Uri;
        }

        var project = ResolveProject(endpoint)
                      ?? throw new InvalidOperationException("A Vertex AI connection must name its GCP project as a 'project' query parameter.");
        var location = LocationFromHost(baseUri.Host)
                       ?? throw new InvalidOperationException(
                           "A Vertex AI connection must name its location in the endpoint host, for example "
                           + "https://europe-west4-aiplatform.googleapis.com.");

        var qualified = modelPath.StartsWith("publishers/", StringComparison.OrdinalIgnoreCase)
            ? modelPath
            : $"publishers/google/{modelPath}";

        builder.Path = $"{EnsureVersion(basePath, "v1")}/projects/{project}/locations/{location}/{qualified}:{method}";
        return builder.Uri;
    }

    /// <summary>Builds the URI that lists the models an endpoint exposes, or <see langword="null" /> on Vertex, which has no such list.</summary>
    /// <param name="endpoint">The stored provider endpoint.</param>
    public static Uri? BuildModelsUri(ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (IsVertex(endpoint.BaseUrl))
        {
            // Vertex lists publisher models through a separate API surface that a generateContent endpoint says
            // nothing about, so discovery there is manual rather than guessed at.
            return null;
        }

        var builder = new UriBuilder(new Uri(endpoint.BaseUrl, UriKind.Absolute));
        builder.Path = $"{EnsureVersion(builder.Path.TrimEnd('/'), "v1beta")}/models";
        return builder.Uri;
    }

    // The base URL may or may not already carry the API version; adding a second one addresses nothing.
    private static string EnsureVersion(string path, string version)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1].StartsWith('v') && segments[^1].Length <= 8
            ? path
            : $"{path}/{version}";
    }
}
