// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     One resolved embedding runtime ready for execution.
/// </summary>
public interface IResolvedAiEmbeddingRuntime
{
    /// <summary>Gets the resolved AI connection profile.</summary>
    AiConnectionDto Connection { get; }

    /// <summary>Gets the resolved configured model.</summary>
    AiConfiguredModelDto Model { get; }

    /// <summary>Gets the resolved purpose binding.</summary>
    AiPurposeBindingDto Binding { get; }

    /// <summary>Gets the embedding generator ready for execution.</summary>
    IEmbeddingGenerator<string, Embedding<float>> Generator { get; }

    /// <summary>Gets the tokenizer name used for token counting.</summary>
    string TokenizerName { get; }

    /// <summary>Gets the resolved embedding vector width.</summary>
    int Dimensions { get; }

    /// <summary>
    ///     Gets the logical-model role this runtime was resolved from, or <see langword="null" /> when resolved
    ///     directly from a purpose binding / raw model. Recorded with usage so token spend can be attributed to the
    ///     logical model that produced it.
    /// </summary>
    string? LogicalModelName { get; }
}
