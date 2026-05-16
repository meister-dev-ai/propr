// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;

internal sealed class ReviewPipelineRunner<TContext>(IEnumerable<IReviewPipelineStage<TContext>> stages) : IReviewPipeline<TContext>
{
    private readonly IReadOnlyDictionary<string, IReviewPipelineStage<TContext>> _stages = stages.ToDictionary(
        stage => stage.StageId,
        StringComparer.Ordinal);

    public Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken)
    {
        return this.ExecuteAsync(context, this._stages.Keys, cancellationToken);
    }

    public async Task<TContext> ExecuteAsync(
        TContext context,
        IEnumerable<string> stageIds,
        CancellationToken cancellationToken)
    {
        var current = context;

        foreach (var stageId in stageIds)
        {
            if (!this._stages.TryGetValue(stageId, out var stage))
            {
                throw new InvalidOperationException($"No Reviewing pipeline stage is registered for '{stageId}'.");
            }

            current = await stage.ExecuteAsync(current, cancellationToken);
        }

        return current;
    }
}
