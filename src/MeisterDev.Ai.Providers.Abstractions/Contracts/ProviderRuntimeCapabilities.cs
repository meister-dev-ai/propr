// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     What a resolved provider runtime can do. Every flag is a fact about the provider and the protocol it was
///     bound to, carrying no notion of what the host intends to use it for; a host maps this onto whatever
///     capability shape its own workloads expect.
/// </summary>
/// <param name="SupportsProviderManagedSessions">The provider can hold conversation state server-side rather than requiring the full history each turn.</param>
/// <param name="SupportsManagedRemoteConversation">The provider exposes a managed remote conversation the host can continue by handle.</param>
/// <param name="SupportsBackgroundResponses">The provider can produce a response asynchronously and be polled for completion.</param>
/// <param name="PrefersResponsesApi">The binding resolves to the Responses-style protocol rather than chat completions.</param>
/// <param name="SupportsPromptCaching">The provider can serve part of a prompt from its own cache.</param>
/// <param name="SupportsPromptCacheRouting">The provider accepts a routing hint that keeps related calls on the same cache.</param>
public sealed record ProviderRuntimeCapabilities(
    bool SupportsProviderManagedSessions,
    bool SupportsManagedRemoteConversation,
    bool SupportsBackgroundResponses,
    bool PrefersResponsesApi,
    bool SupportsPromptCaching = false,
    bool SupportsPromptCacheRouting = false)
{
    /// <summary>A runtime with none of the optional provider capabilities.</summary>
    public static ProviderRuntimeCapabilities None { get; } = new(false, false, false, false);
}
