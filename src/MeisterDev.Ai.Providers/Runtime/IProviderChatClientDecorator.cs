// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Runtime;

/// <summary>
///     One stage wrapped around a provider's chat client. A decorator declares which stage it belongs to and the
///     pipeline places it accordingly; it does not choose its own position. This is how behaviour the library has
///     no business knowing about — spend limits, a host's telemetry sink — is contributed from outside without the
///     library taking a dependency on it.
/// </summary>
public interface IProviderChatClientDecorator
{
    /// <summary>The stage this decorator occupies.</summary>
    ProviderRuntimeStage Stage { get; }

    /// <summary>
    ///     Wraps <paramref name="inner" />, or returns it unchanged when this decorator does not apply to the
    ///     given endpoint and model.
    /// </summary>
    /// <param name="inner">The client to wrap.</param>
    /// <param name="endpoint">The endpoint the call targets.</param>
    /// <param name="model">The model the call is bound to.</param>
    IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model);
}
