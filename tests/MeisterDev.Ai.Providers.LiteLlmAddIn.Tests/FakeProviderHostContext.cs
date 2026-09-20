// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.LiteLlmAddIn.Tests;

/// <summary>
///     Stands in for what a host allows this family to do against one connection, supplying the transport and
///     nothing else.
/// </summary>
/// <remarks>
///     The four primitives this family never asks for throw rather than returning a stub, so a call that started
///     using one would be visible here instead of passing against a stand-in.
/// </remarks>
/// <param name="wire">What the clients this context hands out send to.</param>
internal sealed class FakeProviderHostContext(HttpMessageHandler wire) : IProviderConnectionContext
{
    /// <inheritdoc />
    public IProviderHttpClientFactory Http { get; } = new Factory(wire);

    /// <inheritdoc />
    public IProviderCredentialSessions Credentials =>
        throw new NotSupportedException("This family reads its credential from the endpoint.");

    /// <inheritdoc />
    public IProviderLeases Leases => throw new NotSupportedException("This family arbitrates no resource.");

    /// <inheritdoc />
    public IProviderKeyedStore Store => throw new NotSupportedException("This family keeps no state between calls.");

    /// <inheritdoc />
    public IProviderHealthSignal Health =>
        throw new NotSupportedException("This family reports no credential health.");

    /// <summary>Hands out clients over one handler, composing whatever outer handlers the family contributes.</summary>
    /// <param name="wire">What the clients it builds send to.</param>
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

            // Not disposed with the client: one handler serves every client this factory hands out, as the
            // host's pooled chain does.
            return new HttpClient(new Undisposed(pipeline), disposeHandler: true);
        }
    }

    /// <summary>Keeps disposing a client from tearing down the handler the next one needs.</summary>
    /// <param name="inner">The shared handler.</param>
    private sealed class Undisposed(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            // The base disposes what it wraps, and what it wraps is shared.
        }
    }
}
