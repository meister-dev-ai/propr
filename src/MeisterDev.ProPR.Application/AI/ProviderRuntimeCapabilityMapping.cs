// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Maps the provider library's capability report onto the review-side shape. The flags are the same facts
///     either way; this exists so the provider seam stays free of review vocabulary while the review path keeps
///     consuming a type it owns.
/// </summary>
public static class ProviderRuntimeCapabilityMapping
{
    /// <summary>Projects provider capabilities onto the review runtime's capability record.</summary>
    /// <param name="capabilities">Capabilities reported by the provider driver.</param>
    public static AgentReviewRuntimeCapabilities ToReviewCapabilities(this ProviderRuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return new AgentReviewRuntimeCapabilities(
            capabilities.SupportsProviderManagedSessions,
            capabilities.SupportsManagedRemoteConversation,
            capabilities.SupportsBackgroundResponses,
            capabilities.PrefersResponsesApi,
            capabilities.SupportsPromptCaching,
            capabilities.SupportsPromptCacheRouting);
    }
}
