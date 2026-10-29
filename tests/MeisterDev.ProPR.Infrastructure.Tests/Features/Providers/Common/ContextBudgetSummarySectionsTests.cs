// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Xunit;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

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

    // ProPR writes the tenant's maximum review duration into the summary of a review that ended at it, followed by
    // the files the review did not reach, so a reader can tell a shortened review from a complete one.
    [Fact]
    public void Append_StatesTheTimeLimitAndListsTheFilesNotReviewed_WhenTheTimeLimitWasReached()
    {
        var result = new ReviewResult("summary", [])
        {
            TimeLimitReached = true,
            TimeLimitSeconds = 1800,
            TimeLimitSkippedFilePaths = ["src/a.cs", "src/b.cs"],
        };

        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, result);
        var output = builder.ToString();

        Assert.Contains("Time limit reached", output);
        Assert.Contains("tenant's time limit of 30 minutes", output);
        Assert.Contains("2 files were not reviewed", output);
        Assert.Contains("- src/a.cs", output);
        Assert.Contains("- src/b.cs", output);
    }

    [Theory]
    [InlineData(90, 1, false, "time limit of 90 seconds", "1 file was not reviewed.")]
    [InlineData(3600, 0, true, "time limit of 1 hour", "Every file was reviewed. The pull-request-wide passes did not run.")]
    [InlineData(7200, 3, false, "time limit of 2 hours", "3 files were not reviewed.")]
    [InlineData(60, 2, true, "time limit of 1 minute", "2 files were not reviewed. The pull-request-wide passes did not run.")]
    public void FormatTimeLimitNote_NamesTheDurationAndWhatTheReviewLeftOut(
        int seconds,
        int skippedFiles,
        bool skippedPrWidePasses,
        string expectedLimit,
        string expectedOutcome)
    {
        var result = new ReviewResult("summary", [])
        {
            TimeLimitReached = true,
            TimeLimitSeconds = seconds,
            TimeLimitSkippedFilePaths = [.. Enumerable.Range(1, skippedFiles).Select(index => $"src/{index}.cs")],
            TimeLimitSkippedPrWidePasses = skippedPrWidePasses,
        };

        var note = ContextBudgetSummarySections.FormatTimeLimitNote(result);

        Assert.Contains(expectedLimit, note);
        Assert.EndsWith(expectedOutcome, note);
    }

    [Fact]
    public void Append_WritesNothing_WhenNotBudgetSoftCappedAndNoContextOutcomes()
    {
        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, new ReviewResult("summary", []));

        Assert.Equal(string.Empty, builder.ToString());
    }
}
