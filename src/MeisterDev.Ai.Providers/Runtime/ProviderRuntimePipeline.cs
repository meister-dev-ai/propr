// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Runtime;

/// <summary>
///     Composes the decorators around a provider's chat client in <see cref="ProviderRuntimeStage" /> order.
///     Registration order is deliberately irrelevant: the stage a decorator declares decides where it lands, so
///     adding one cannot silently reorder the others.
/// </summary>
public sealed class ProviderRuntimePipeline(IEnumerable<IProviderChatClientDecorator> decorators)
{
    private readonly IProviderChatClientDecorator[] _decorators = decorators
        .OrderByDescending(decorator => (int)decorator.Stage)
        .ToArray();

    /// <summary>
    ///     Wraps <paramref name="client" /> so the outermost stage is entered first. Decorators are applied
    ///     innermost-first, which yields that ordering.
    /// </summary>
    /// <param name="client">The driver's chat client.</param>
    /// <param name="endpoint">The endpoint the call targets.</param>
    /// <param name="model">The model the call is bound to.</param>
    public IChatClient Compose(IChatClient client, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(client);

        var composed = client;
        foreach (var decorator in this._decorators)
        {
            composed = decorator.Decorate(composed, endpoint, model);
        }

        return composed;
    }
}
