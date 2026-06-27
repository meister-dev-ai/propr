// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The per-purpose model selection active for the current offline review run. Carries the single shared
///     chat client (every tier targets the same offline endpoint, switching only the deployment per request)
///     together with the configured tiered model map and the run's primary model used as the final fallback.
/// </summary>
public sealed record OfflineTierModelSelection(
    IChatClient ChatClient,
    EvaluationTieredModels Tiers,
    string? PrimaryModelId);
