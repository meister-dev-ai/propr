// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Supported prompt experiment stage catalog entry.
/// </summary>
public sealed record PromptStageDefinition(
    string StageKey,
    string Label,
    string StrategyScope,
    PromptStageRole PromptRole,
    string DefaultSource);
