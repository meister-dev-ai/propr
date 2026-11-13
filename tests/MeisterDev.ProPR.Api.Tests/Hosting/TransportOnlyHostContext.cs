// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Tests.Hosting;

/// <summary>Supplies the transport and nothing else, which is all the OpenAI family asks of a host.</summary>
/// <param name="wire">What the clients this context hands out send to.</param>
internal sealed class TransportOnlyHostContext(HttpMessageHandler wire) : IProviderConnectionContext
{
    public IProviderHttpClientFactory Http { get; } = new Factory(wire);

    public IProviderCredentialSessions Credentials =>
        throw new NotSupportedException("The OpenAI family reads its credential from the endpoint.");

    public IProviderLeases Leases => throw new NotSupportedException("The OpenAI family arbitrates no resource.");

    public IProviderKeyedStore Store => throw new NotSupportedException("The OpenAI family keeps no state between calls.");

    public IProviderHealthSignal Health => throw new NotSupportedException("The OpenAI family reports no credential health.");

    private sealed class Factory(HttpMessageHandler wire) : IProviderHttpClientFactory
    {
        public HttpClient Create(ProviderHttpPurpose purpose, IReadOnlyList<DelegatingHandler>? outerHandlers = null)
        {
            var pipeline = wire;
            for (var index = outerHandlers?.Count - 1; index >= 0; index--)
            {
                var handler = outerHandlers![index.Value];
                handler.InnerHandler = pipeline;
                pipeline = handler;
            }

            return new HttpClient(pipeline, disposeHandler: false);
        }
    }
}
