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

    // The note that a review reached the tenant's maximum review duration is part of the summary text. ProPR lists
    // the files the review did not reach under a heading in the posted summary and does not repeat the note there.
    [Fact]
    public void Append_ListsTheFilesNotReviewed_WhenTheMaximumReviewDurationWasReached()
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

        Assert.Contains("**Not reviewed because the maximum review duration was reached** (2 files)", output);
        Assert.Contains("- src/a.cs", output);
        Assert.Contains("- src/b.cs", output);
        Assert.DoesNotContain(ContextBudgetSummarySections.FormatTimeLimitNote(result), output);
    }

    // A review that reached the maximum review duration after its last file skipped only the pull-request-wide
    // passes. There is no file to list, and the note in the summary text contains the rest.
    [Fact]
    public void Append_WritesNothingForTheMaximumReviewDuration_WhenNoFileWasLeftOut()
    {
        var result = new ReviewResult("summary", [])
        {
            TimeLimitReached = true,
            TimeLimitSeconds = 1800,
            TimeLimitSkippedPrWidePasses = true,
        };

        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, result);

        Assert.Equal(string.Empty, builder.ToString());
    }

    [Theory]
    [InlineData(90, 1, false, "maximum review duration of 90 seconds", "so 1 file was not reviewed.")]
    [InlineData(
        3600, 0, true, "maximum review duration of 1 hour", "1 hour. No file was left out because of this limit. The pull-request-wide passes did not run.")]
    [InlineData(7200, 3, false, "maximum review duration of 2 hours", "so 3 files were not reviewed.")]
    [InlineData(60, 2, true, "maximum review duration of 1 minute", "so 2 files were not reviewed. The pull-request-wide passes did not run.")]
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

        Assert.StartsWith("**Maximum review duration reached.**", note);
        Assert.Contains(expectedLimit, note);
        Assert.EndsWith(expectedOutcome, note);
    }

    [Theory]
    [InlineData(1, "1 file was only moved or renamed, without changes to its content, so ProPR did not review it.")]
    [InlineData(438, "438 files were only moved or renamed, without changes to their content, so ProPR did not review them.")]
    public void FormatUnchangedMovesNote_StatesHowManyFilesWereOnlyMoved(int count, string expectedSentence)
    {
        var note = ContextBudgetSummarySections.FormatUnchangedMovesNote(count);

        Assert.EndsWith(expectedSentence, note);
    }

    // ProPR lists the files whose review failed, so a reader can tell a review that left files out from a review
    // that found nothing in them.
    [Fact]
    public void Append_ListsTheFilesWhoseReviewFailed()
    {
        var result = new ReviewResult("summary", []) { FailedReviewFilePaths = ["src/a.cs", "src/b.cs"] };

        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, result);
        var output = builder.ToString();

        Assert.Contains("Review failed for 2 files", output);
        Assert.Contains("- src/a.cs", output);
        Assert.Contains("- src/b.cs", output);
    }

    [Fact]
    public void Append_UsesSingularFileWord_ForOneFailedFileReview()
    {
        var result = new ReviewResult("summary", []) { FailedReviewFilePaths = ["src/only.cs"] };

        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, result);
        var output = builder.ToString();

        Assert.Contains("Review failed for 1 file.", output);
        Assert.Contains("- src/only.cs", output);
    }

    [Fact]
    public void Append_WritesNothing_WhenNotBudgetSoftCappedAndNoContextOutcomes()
    {
        var builder = new StringBuilder();
        ContextBudgetSummarySections.Append(builder, new ReviewResult("summary", []));

        Assert.Equal(string.Empty, builder.ToString());
    }
}
