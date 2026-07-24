// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     The result of resolving a named logical model to a chat runtime: the runtime itself plus the role name, the
///     layer that resolved it, and the reasoning effort carried on the mapping. Effort is surfaced separately because
///     <see cref="IResolvedAiChatRuntime" /> does not carry it — it is applied per model call by the pass plumbing.
/// </summary>
public sealed record ResolvedLogicalModelChatRuntime(
    IResolvedAiChatRuntime Runtime,
    string RoleName,
    LogicalModelLayer Layer,
    ReviewReasoningEffort ReasoningEffort);

/// <summary>
///     The result of resolving a named logical model to an embedding runtime: the runtime plus the role name and the
///     layer that resolved it. Embeddings do not carry reasoning effort.
/// </summary>
public sealed record ResolvedLogicalModelEmbeddingRuntime(
    IResolvedAiEmbeddingRuntime Runtime,
    string RoleName,
    LogicalModelLayer Layer);
