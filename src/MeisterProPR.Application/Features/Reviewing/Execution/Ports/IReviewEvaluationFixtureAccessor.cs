// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Scoped accessor that carries the active offline fixture through one harness execution.
/// </summary>
public interface IReviewEvaluationFixtureAccessor
{
    /// <summary>Gets or sets the active fixture for the current execution scope.</summary>
    ReviewEvaluationFixture? Fixture { get; set; }
}
