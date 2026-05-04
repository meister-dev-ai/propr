// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Verification;

public sealed class SummaryReconciliationResultTests
{
    [Fact]
    public void Constructor_WithValidInputs_PreservesReconciliationMetadata()
    {
        var result = new SummaryReconciliationResult(
            "Original summary.",
            "Final summary.",
            ["finding-drop-1"],
            ["finding-summary-1"],
            true,
            "deterministic_summary_rewrite");

        Assert.Equal("Original summary.", result.OriginalSummary);
        Assert.Equal("Final summary.", result.FinalSummary);
        Assert.Contains("finding-drop-1", result.DroppedFindingIds);
        Assert.Contains("finding-summary-1", result.SummaryOnlyFindingIds);
        Assert.True(result.RewritePerformed);
        Assert.Equal("deterministic_summary_rewrite", result.RuleSource);
    }

    [Fact]
    public void Constructor_WithoutRuleSource_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SummaryReconciliationResult(
            "Original summary.",
            "Final summary.",
            [],
            [],
            false,
            string.Empty));
    }
}
