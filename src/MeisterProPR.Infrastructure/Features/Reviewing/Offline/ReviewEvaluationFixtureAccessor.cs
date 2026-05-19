// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Scoped holder for the active fixture used by offline Reviewing services.
/// </summary>
public sealed class ReviewEvaluationFixtureAccessor : IReviewEvaluationFixtureAccessor
{
    /// <inheritdoc />
    public ReviewEvaluationFixture? Fixture { get; set; }

    /// <inheritdoc />
    public string? ScenarioId { get; set; }

    /// <inheritdoc />
    public FixtureScenario? Scenario => string.IsNullOrWhiteSpace(this.ScenarioId)
        ? null
        : this.Fixture?.ScenariosOrEmpty.FirstOrDefault(scenario =>
            string.Equals(scenario.ScenarioId, this.ScenarioId, StringComparison.Ordinal));
}
