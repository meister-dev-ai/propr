// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     What one model call consumed, in the terms the job breakdown records. A step that calls a model outside a
///     review pass returns this to its caller, and the caller adds it to the protocol of the pass that owns the step.
/// </summary>
/// <param name="ModelId">The model that answered, as the provider names it.</param>
/// <param name="LogicalModelName">The logical model it resolved through, when it resolved through one.</param>
public sealed record ModelCallSpend(
    string ModelId,
    string? LogicalModelName,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long ReasoningTokens);
