// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Runs one fixture repeatedly under named prompt variants and writes one artifact per run.
/// </summary>
public interface IPromptExperimentBatchRunner
{
    /// <summary>
    ///     Executes the given prompt experiment batch by running the specified review evaluation fixture with the defined stage prompt variants and configuration,
    ///     using the provided job template as a basis for the review jobs to be executed. The method returns a PromptExperimentBatchResult containing the evidence
    ///     records for each stage of the workflow execution,
    ///     which can then be used for analysis and comparison of the different prompt variants. The cancellation token can be used to cancel the execution of the
    ///     batch if needed.
    /// </summary>
    /// <param name="batch">The prompt experiment batch to be executed.</param>
    /// <param name="fixture">The review evaluation fixture to be used for the batch execution.</param>
    /// <param name="configuration">The evaluation configuration to be applied during the batch execution.</param>
    /// <param name="jobTemplate">The review job template to be used as a basis for the review jobs.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the prompt experiment batch result.</returns>
    Task<PromptExperimentBatchResult> RunAsync(
        PromptExperimentBatch batch,
        ReviewEvaluationFixture fixture,
        EvaluationConfiguration configuration,
        ReviewJob jobTemplate,
        CancellationToken cancellationToken = default);
}
