// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.ValueObjects;

public sealed class ProviderReadinessCriterionResultTests
{
    [Fact]
    public void Constructor_WithValidArguments_PersistsValues()
    {
        var result = new ProviderReadinessCriterionResult(
            "connection.reviewerIdentity",
            "connection",
            "unsatisfied",
            "Configured reviewer identity is required for workflow-complete readiness.");

        Assert.Equal("connection.reviewerIdentity", result.CriterionKey);
        Assert.Equal("connection", result.Scope);
        Assert.Equal("unsatisfied", result.Status);
    }

    [Fact]
    public void Constructor_WithMissingSummary_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProviderReadinessCriterionResult("criterion", "scope", "status", string.Empty));
    }
}
