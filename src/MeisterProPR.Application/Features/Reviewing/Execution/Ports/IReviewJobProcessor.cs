// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reviewing-owned boundary for processing a single review job.
/// </summary>
public interface IReviewJobProcessor
{
    /// <summary>
    ///     Processes the given review job end-to-end.
    /// </summary>
    Task ProcessAsync(ReviewJob job, CancellationToken ct);
}
