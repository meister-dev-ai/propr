// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

internal static class PromptStageEvidenceRecorder
{
    public static async Task RecordAsync(
        ReviewSystemContext context,
        string stageKey,
        string? systemPromptText,
        string? userPromptText,
        CancellationToken ct)
    {
        if (!context.ActiveProtocolId.HasValue || context.ProtocolRecorder is null || context.PromptExperiment is null)
        {
            return;
        }

        if (!PromptStageCatalog.TryGet(stageKey, out var definition) || definition is null)
        {
            return;
        }

        context.PromptExperiment.TryGetVariant(stageKey, definition.PromptRole, out var variant);
        var task = context.ProtocolRecorder.RecordPromptStageEvidenceAsync(
            context.ActiveProtocolId.Value,
            stageKey,
            context.PromptExperiment.VariantName,
            variant?.CompositionMode ?? PromptCompositionMode.Default,
            variant is null,
            systemPromptText,
            userPromptText,
            ct);

        if (task is not null)
        {
            await task;
        }
    }
}
