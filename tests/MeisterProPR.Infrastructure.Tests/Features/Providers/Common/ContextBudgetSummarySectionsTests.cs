// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class ContextBudgetSummarySectionsTests
{
    [Fact]
    public void Append_AddsSoftCapNoteAndSkippedFiles_WhenBudgetSoftCapped()
    {
        var result = new ReviewResult("summary", [])
        {
            BudgetSoftCapped = true,
            BudgetSoftCapThresholdUsd = 5m,
            BudgetSoftCapSpentUsd = 6.5m,
            BudgetSoftCapSkippedFilePaths = ["src/a.cs", "src/b.cs"],
        };

        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, result);
        var output = builder.ToString();

        Assert.Contains("Budget soft cap reached", output);
        Assert.Contains("$5.00 cap", output);
        Assert.Contains("$6.50 spent", output);
        Assert.Contains("2 files not reviewed", output);
        Assert.Contains("- src/a.cs", output);
        Assert.Contains("- src/b.cs", output);
    }

    [Fact]
    public void Append_UsesSingularFileWord_ForOneSkippedFile()
    {
        var result = new ReviewResult("summary", [])
        {
            BudgetSoftCapped = true,
            BudgetSoftCapThresholdUsd = 2m,
            BudgetSoftCapSpentUsd = 2m,
            BudgetSoftCapSkippedFilePaths = ["src/only.cs"],
        };

        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, result);

        Assert.Contains("1 file not reviewed", builder.ToString());
    }

    [Fact]
    public void Append_WritesNothing_WhenNotBudgetSoftCappedAndNoContextOutcomes()
    {
        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, new ReviewResult("summary", []));

        Assert.Equal(string.Empty, builder.ToString());
    }
}
