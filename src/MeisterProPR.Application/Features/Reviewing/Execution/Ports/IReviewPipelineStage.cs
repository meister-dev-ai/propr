// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Represents one ordered stage in a Reviewing pipeline.</summary>
/// <typeparam name="TContext">Pipeline context type.</typeparam>
public interface IReviewPipelineStage<TContext>
{
    /// <summary>Stable stage identifier used for profile composition.</summary>
    string StageId { get; }

    /// <summary>Executes the stage and returns the updated context.</summary>
    Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken);
}
