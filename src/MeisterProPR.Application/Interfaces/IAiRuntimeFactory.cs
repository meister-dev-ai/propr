// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Builds an execution-ready AI runtime from an already-resolved connection + configured model + binding triple,
///     applying provider driver construction and any budget metering. This is the provider-neutral construction step
///     shared by purpose/model resolution and logical-model resolution — it does no lookup of its own.
/// </summary>
public interface IAiRuntimeFactory
{
    /// <summary>Builds a chat runtime for the given connection/model/binding triple.</summary>
    IResolvedAiChatRuntime CreateChatRuntime(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        string? logicalModelName = null);

    /// <summary>Builds an embedding runtime for the given connection/model/binding triple and dimension count.</summary>
    IResolvedAiEmbeddingRuntime CreateEmbeddingRuntime(
        AiConnectionDto connection,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding,
        string tokenizerName,
        int dimensions,
        string? logicalModelName = null);
}
