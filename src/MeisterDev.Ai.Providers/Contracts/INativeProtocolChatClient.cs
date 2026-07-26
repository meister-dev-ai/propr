// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     A chat client that speaks a provider's own protocol rather than going through a vendor client library.
/// </summary>
/// <remarks>
///     Exists so a caller can shape a request without naming a provider. Microsoft.Extensions.AI hands
///     <see cref="ChatOptions.RawRepresentationFactory" /> the client it is building for, so a caller that finds
///     this interface knows it may pass a neutral <see cref="ProviderReasoningRequest" /> instead of a vendor
///     library's options object — and the driver behind it is the one that knows what to do with it.
/// </remarks>
public interface INativeProtocolChatClient : IChatClient
{
    /// <summary>Gets the protocol this client speaks.</summary>
    AiProtocolMode NativeProtocol { get; }
}
