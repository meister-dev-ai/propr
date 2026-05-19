// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Validates offline prompt experiment batches before review execution begins.
/// </summary>
public interface IReviewPromptExperimentValidator
{
    /// <summary>
    ///     Validates the specified prompt experiment batch asynchronously.
    /// </summary>
    /// <param name="batch">The prompt experiment batch to validate.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ValidateAsync(PromptExperimentBatch batch, CancellationToken cancellationToken = default);
}
