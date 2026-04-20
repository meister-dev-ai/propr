// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests asserting that <see cref="ReviewPrompts.AgenticLoopGuidance" /> contains the
///     certainty gate rules required by feature 023 (IMP-01, IMP-06).
///     These tests were updated from feature 022's findings-validation-rule assertions
///     to reflect the rewritten rule structure (CERTAINTY GATE consolidation).
/// </summary>
public class ReviewPromptsValidationTests
{
    // Certainty Gate must be present and must require direct observation
    [Fact]
    public void AgenticLoopGuidance_ContainsCertaintyGate()
    {
        Assert.Contains("CERTAINTY GATE", ReviewPrompts.AgenticLoopGuidance, StringComparison.OrdinalIgnoreCase);
    }

    // Certainty Gate must state that omission is preferable to speculation
    [Fact]
    public void AgenticLoopGuidance_StatesOmissionPreferableToSpeculation()
    {
        Assert.Contains(
            "Omission is always preferable to speculation",
            ReviewPrompts.AgenticLoopGuidance,
            StringComparison.OrdinalIgnoreCase);
    }

    // Certainty Gate must require the finding to be directly observed
    [Fact]
    public void AgenticLoopGuidance_RequiresDirectObservation()
    {
        Assert.Contains(
            "directly seen the problematic code",
            ReviewPrompts.AgenticLoopGuidance,
            StringComparison.OrdinalIgnoreCase);
    }
}
