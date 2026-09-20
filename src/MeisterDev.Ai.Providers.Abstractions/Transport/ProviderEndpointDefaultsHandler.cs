// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Puts an endpoint's default headers and default query parameters on every request a client library sends.
/// </summary>
/// <remarks>
///     <para>
///         A family that composes its own address calls <see cref="ProviderEndpointAddress.For" /> and sets the
///         headers itself. A client library composes its own requests from the endpoint it was handed, and it
///         knows nothing about either default, so without this the two paths disagree: discovery reaches a
///         gateway that needs a routing header and a model call does not, and a provider carrying its key as a
///         query parameter is authenticated on one and not the other.
///     </para>
///     <para>
///         A header the request already carries is left alone. The client library sets its own authorization and
///         content headers from the credential the host configured, and a default is what an operator added
///         beside that, not a replacement for it.
///     </para>
/// </remarks>
/// <param name="endpoint">The endpoint whose defaults these requests carry.</param>
public sealed class ProviderEndpointDefaultsHandler(ProviderEndpoint endpoint) : DelegatingHandler
{
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var (name, value) in endpoint.DefaultHeaders ?? EmptyHeaders)
        {
            // Both collections are asked, because the outgoing message carries both. A header the caller set on
            // the content is already on the request, and adding the endpoint's value to the request collection
            // would send the name twice with two values.
            if (request.Headers.Contains(name) || request.Content?.Headers.Contains(name) == true)
            {
                continue;
            }

            // Tried on the request first and on the content second, because a content header such as
            // Content-Type is refused on the request's own collection and belongs to the body.
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (request.RequestUri is { } address)
        {
            request.RequestUri = ProviderEndpointAddress.WithDefaultQuery(address, endpoint.DefaultQueryParams);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
