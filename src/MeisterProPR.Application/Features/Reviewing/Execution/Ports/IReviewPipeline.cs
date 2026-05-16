// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Runs one ordered Reviewing pipeline over a specific execution context type.</summary>
/// <typeparam name="TContext">Pipeline context type.</typeparam>
public interface IReviewPipeline<TContext>
{
    /// <summary>Executes the pipeline for the provided context.</summary>
    Task<TContext> ExecuteAsync(TContext context, CancellationToken cancellationToken);

    /// <summary>Executes the requested ordered stages for the provided context.</summary>
    Task<TContext> ExecuteAsync(TContext context, IEnumerable<string> stageIds, CancellationToken cancellationToken);
}
