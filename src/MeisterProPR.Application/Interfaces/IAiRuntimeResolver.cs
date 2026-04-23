// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Resolves provider-neutral AI runtimes for product-owned purposes.
/// </summary>
public interface IAiRuntimeResolver
{
    /// <summary>
    ///     Resolves a chat runtime for the given client and purpose.
    /// </summary>
    Task<IResolvedAiChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default);

    /// <summary>
    ///     Resolves an embedding runtime for the given client and purpose.
    /// </summary>
    Task<IResolvedAiEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        int? expectedDimensions = null,
        CancellationToken ct = default);
}
