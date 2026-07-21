// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.AI;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Domain.Services;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     An embedding-generator decorator that enforces the ambient review job's USD hard cap for the in-review
///     embedding calls (for example the semantic comment screener). Before generating it stops the review when a
///     hard cap has been reached; after generating it prices the reported token usage and records it against the
///     running total. A transparent pass-through when no budget scope is active.
/// </summary>
public sealed class BudgetEnforcingEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
    IBudgetScopeAccessor budgetScopeAccessor,
    ModelPricing pricing) : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
{
    /// <inheritdoc />
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scope = budgetScopeAccessor.Current;
        scope?.ThrowIfHardCapReached();

        var result = await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);

        if (scope is not null)
        {
            var usage = AiTokenUsageExtractor.FromUsage(result.Usage);
            scope.RecordCall(AiCostCalculator.Calculate(usage, pricing).Usd);
        }

        return result;
    }
}
