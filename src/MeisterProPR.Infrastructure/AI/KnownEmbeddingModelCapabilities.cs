// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Built-in embedding metadata used as a backward-compatible fallback for legacy connections
///     that were created before per-deployment capability rows existed.
/// </summary>
internal static class KnownEmbeddingModelCapabilities
{
    private static readonly IReadOnlyDictionary<string, AiConnectionModelCapabilityDto> KnownCapabilities =
        new Dictionary<string, AiConnectionModelCapabilityDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["text-embedding-3-small"] = new("text-embedding-3-small", "cl100k_base", 8192, 1536),
            ["text-embedding-3-large"] = new("text-embedding-3-large", "cl100k_base", 8192, 3072),
            ["text-embedding-ada-002"] = new("text-embedding-ada-002", "cl100k_base", 8192, 1536),
        };

    public static AiConnectionModelCapabilityDto? Get(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        return KnownCapabilities.TryGetValue(modelName.Trim(), out var capability)
            ? capability
            : null;
    }
}
