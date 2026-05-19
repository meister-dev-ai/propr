// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Validates offline prompt experiment batches before execution starts.
/// </summary>
public sealed class ReviewPromptExperimentValidator : IReviewPromptExperimentValidator
{
    public Task ValidateAsync(PromptExperimentBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var variantNames = new HashSet<string>(StringComparer.Ordinal);
        var runIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var run in batch.VariantRunsOrEmpty)
        {
            if (!runIds.Add(run.RunId))
            {
                throw new InvalidOperationException($"Prompt experiment run id '{run.RunId}' must be unique within batch '{batch.BatchId}'.");
            }

            if (!variantNames.Add(run.VariantName))
            {
                throw new InvalidOperationException($"Prompt experiment variant '{run.VariantName}' must be unique within batch '{batch.BatchId}'.");
            }

            var stageRolePairs = new HashSet<(string StageKey, PromptStageRole PromptRole)>();
            foreach (var variant in run.StageVariantsOrEmpty)
            {
                if (!PromptStageCatalog.TryGet(variant.StageKey, out var definition) || definition is null)
                {
                    throw new InvalidOperationException($"Prompt experiment stage '{variant.StageKey}' is not supported.");
                }

                if (definition.PromptRole != variant.PromptRole)
                {
                    throw new InvalidOperationException(
                        $"Prompt experiment stage '{variant.StageKey}' requires prompt role '{definition.PromptRole}', but '{variant.PromptRole}' was provided.");
                }

                if (variant.CompositionMode == PromptCompositionMode.Default)
                {
                    throw new InvalidOperationException($"Prompt experiment stage '{variant.StageKey}' must use replace, prepend, or append composition.");
                }

                if (string.IsNullOrWhiteSpace(variant.Content))
                {
                    throw new InvalidOperationException($"Prompt experiment stage '{variant.StageKey}' must provide non-empty content.");
                }

                if (!stageRolePairs.Add((variant.StageKey, variant.PromptRole)))
                {
                    throw new InvalidOperationException(
                        $"Prompt experiment run '{run.RunId}' contains a duplicate stage/role combination for '{variant.StageKey}'.");
                }
            }
        }

        return Task.CompletedTask;
    }
}
