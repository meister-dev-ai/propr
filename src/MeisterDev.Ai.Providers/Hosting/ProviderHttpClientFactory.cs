// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Hands a provider family a client on one of the host's named pipelines, with the family's own handlers
///     wrapped outside the host's.
/// </summary>
/// <remarks>
///     <para>
///         The pipeline comes from the host's handler pool, so every client a family takes shares the pooled
///         connections and the name-resolution rotation that pool manages. Handing out a handler instead, or
///         letting a family build its own, would pin resolution for the life of the process and leak sockets.
///     </para>
///     <para>
///         The host's part of the pipeline is innermost and is not optional: the connect-time address check that
///         defends against an operator-supplied base URL sits on the pooled handler, and a family's handlers sit
///         outside it. A family can therefore see and change what it sends and what comes back, and cannot remove
///         the check or reach an address the installation's egress policy refuses.
///     </para>
/// </remarks>
/// <param name="handlerFactory">The host's pooled handler chains, by pipeline name.</param>
public sealed class ProviderHttpClientFactory(IHttpMessageHandlerFactory handlerFactory) : IProviderHttpClientFactory
{
    /// <summary>The contract's name for the handler list, used where a refusal has to name it.</summary>
    private const string OuterHandlersParameter = "outerHandlers";

    /// <inheritdoc />
    public HttpClient Create(ProviderHttpPurpose purpose, IReadOnlyList<DelegatingHandler>? outerHandlers = null)
    {
        var name = ProviderHttpPipelines.NameFor(purpose);

        // Every handler is checked before any of them is touched, and before a pooled chain is taken. Checking as
        // each one is placed leaves the ones already placed pointing at the host's pipeline when a later entry is
        // refused, and a handler that has an inner one is refused by the next call as already chained — so a
        // rejected call would leave the caller's own handlers unusable.
        for (var index = 0; index < outerHandlers?.Count; index++)
        {
            Accepted(outerHandlers, index);
        }

        // Not disposed with the client: the chain belongs to the host's pool, which rotates and disposes it on
        // its own schedule. Disposing it here would tear down connections other callers are still using.
        HttpMessageHandler pipeline = new PooledPipeline(handlerFactory.CreateHandler(name));

        if (outerHandlers is { Count: > 0 })
        {
            // Reversed, so the first handler a family listed is the outermost one and sees a request before the
            // rest of its own do. That is the order the host's own pipelines are composed in.
            for (var index = outerHandlers.Count - 1; index >= 0; index--)
            {
                var handler = outerHandlers[index];
                handler.InnerHandler = pipeline;
                pipeline = handler;
            }
        }

        // Disposing the client disposes the family's handlers, which it passed here and cannot reach again, and
        // stops at the pooled chain.
        var client = new HttpClient(pipeline, disposeHandler: true);

        if (ProviderHttpPipelines.TimeoutFor(purpose) is { } timeout)
        {
            client.Timeout = timeout;
        }

        return client;
    }

    /// <summary>One of the family's handlers, checked for the two ways a chain can be built wrong.</summary>
    /// <param name="handlers">The handlers as the family listed them.</param>
    /// <param name="index">Which one is being placed.</param>
    /// <exception cref="ArgumentException">The entry is absent, already chained, or listed twice.</exception>
    private static DelegatingHandler Accepted(IReadOnlyList<DelegatingHandler> handlers, int index)
    {
        var handler = handlers[index]
                      ?? throw new ArgumentException(
                          $"Handler {index} is null. Every handler in the list is placed in the pipeline, so "
                          + "there is nothing to put in its position.",
                          OuterHandlersParameter);

        // Both of these produce a pipeline that does something other than what the list says, and neither
        // reports itself: a handler that already has an inner one is part of another chain, and one listed twice
        // would be made to contain itself.
        if (handler.InnerHandler is not null)
        {
            throw new ArgumentException(
                $"Handler {index} ('{handler.GetType().Name}') is already chained to another handler. A handler "
                + "belongs to one client, and the host sets what each one wraps.",
                OuterHandlersParameter);
        }

        for (var earlier = 0; earlier < index; earlier++)
        {
            if (ReferenceEquals(handlers[earlier], handler))
            {
                throw new ArgumentException(
                    $"Handler {index} ('{handler.GetType().Name}') is the same instance as handler {earlier}. "
                    + "A handler placed twice would wrap itself.",
                    OuterHandlersParameter);
            }
        }

        return handler;
    }

    /// <summary>
    ///     The host's pooled chain, held so that disposing the client stops here instead of tearing down
    ///     connections the pool is still lending to other callers.
    /// </summary>
    /// <param name="pooled">The chain the handler pool supplied.</param>
    private sealed class PooledPipeline(HttpMessageHandler pooled) : DelegatingHandler(pooled)
    {
        protected override void Dispose(bool disposing)
        {
            // The base disposes what it wraps, and what it wraps belongs to the host's pool.
        }
    }
}
