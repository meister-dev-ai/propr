// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Validates an offline review fixture before review execution begins.
/// </summary>
public interface IReviewEvaluationFixtureValidator
{
    /// <summary>
    ///     Validates the supplied fixture and throws when it is not internally consistent.
    /// </summary>
    Task ValidateAsync(ReviewEvaluationFixture fixture, CancellationToken cancellationToken = default);
}
