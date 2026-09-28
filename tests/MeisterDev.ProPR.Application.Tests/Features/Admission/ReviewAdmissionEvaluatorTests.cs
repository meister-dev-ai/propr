// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Admission;
using MeisterDev.ProPR.Application.Features.Admission.Models;
using Xunit;

namespace MeisterDev.ProPR.Application.Tests.Features.Admission;

public sealed class ReviewAdmissionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_AdmitsWhenNoBoundIsSet()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            ReviewAdmissionPolicy.None,
            new ReviewAdmissionMeasurement(10_000, 2_000_000, 500_000_000, 99),
            Now);

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public void Evaluate_AdmitsAtTheBound()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxChangedFiles: 150),
            new ReviewAdmissionMeasurement(ChangedFiles: 150),
            Now);

        Assert.True(decision.IsAdmitted);
    }

    [Fact]
    public void Evaluate_RefusesOverTheChangedFileBound_NamingTheValueTheBoundAndTheWayForward()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxChangedFiles: 150),
            new ReviewAdmissionMeasurement(ChangedFiles: 312),
            Now);

        Assert.Equal(ReviewAdmissionOutcome.Refuse, decision.Outcome);
        Assert.Equal(
            "Review not started: 312 changed files exceed the limit of 150. Split the pull request or raise the limit.",
            decision.Reason);
    }

    [Fact]
    public void Evaluate_RefusesOverTheChangedLineBound()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxChangedLines: 20_000),
            new ReviewAdmissionMeasurement(ChangedLines: 20_001),
            Now);

        Assert.Equal(ReviewAdmissionOutcome.Refuse, decision.Outcome);
        Assert.Contains("changed lines", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RefusesOverTheDiffByteBound()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxDiffBytes: 4_000_000),
            new ReviewAdmissionMeasurement(DiffBytes: 4_000_001),
            Now);

        Assert.Equal(ReviewAdmissionOutcome.Refuse, decision.Outcome);
        Assert.Contains("bytes", decision.Reason, StringComparison.Ordinal);
    }

    // The repository dimension is not measured before a review: workspace preparation stops the transfer that
    // passes the bound and reports both numbers, and the refusal is written from them.
    [Fact]
    public void RefuseOversizedRepository_NamesTheMeasuredSizeTheBoundAndTheRemedy()
    {
        var decision = ReviewAdmissionEvaluator.RefuseOversizedRepository(4_096, 2_048);

        Assert.Equal(ReviewAdmissionOutcome.Refuse, decision.Outcome);
        Assert.Contains("4,096 MB", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("2,048 MB", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("Raise the limit", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_HoldsOverTheHourlyReviewBound_UntilTheWindowHasPassed()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxReviewsPerPullRequestPerHour: 4),
            new ReviewAdmissionMeasurement(ReviewsStartedInWindow: 4),
            Now);

        Assert.Equal(ReviewAdmissionOutcome.Hold, decision.Outcome);
        Assert.Equal(Now + ReviewAdmissionEvaluator.BurstWindow, decision.AdmissibleAt);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Evaluate_EndsTheHoldWhenTheOldestReviewLeavesTheWindow()
    {
        // The fourth review was submitted 59 minutes ago, so the window frees up in a minute. Holding for a
        // full hour from now would make the job wait out an hour it has almost finished waiting.
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxReviewsPerPullRequestPerHour: 4),
            new ReviewAdmissionMeasurement(
                ReviewsStartedInWindow: 4,
                OldestReviewSubmittedInWindow: Now.AddMinutes(-59)),
            Now);

        Assert.Equal(ReviewAdmissionOutcome.Hold, decision.Outcome);
        Assert.Equal(Now.AddMinutes(1), decision.AdmissibleAt);
    }

    [Fact]
    public void Evaluate_AdmitsWhenTheOldestReviewIsExactlyOneWindowOld()
    {
        // The window ends at this very instant. A decision holding it would say the review waits for a moment
        // that is not ahead of it, and nothing would ever release it.
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxReviewsPerPullRequestPerHour: 4),
            new ReviewAdmissionMeasurement(
                ReviewsStartedInWindow: 4,
                OldestReviewSubmittedInWindow: Now - ReviewAdmissionEvaluator.BurstWindow),
            Now);

        Assert.True(decision.IsAdmitted);
        Assert.Null(decision.AdmissibleAt);
    }

    [Fact]
    public void Evaluate_AdmitsWhenTheOldestReviewIsOlderThanTheWindow()
    {
        // The window ended a while ago, so the instant a hold would name is behind the present.
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxReviewsPerPullRequestPerHour: 4),
            new ReviewAdmissionMeasurement(
                ReviewsStartedInWindow: 4,
                OldestReviewSubmittedInWindow: Now - ReviewAdmissionEvaluator.BurstWindow - TimeSpan.FromMinutes(30)),
            Now);

        Assert.True(decision.IsAdmitted);
        Assert.Null(decision.AdmissibleAt);
    }

    [Fact]
    public void Evaluate_RefusesSizeBeforeHoldingABurst()
    {
        // A pull request that is too large stays too large after the window, so refusing it first saves the wait.
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxChangedFiles: 150, MaxReviewsPerPullRequestPerHour: 4),
            new ReviewAdmissionMeasurement(ChangedFiles: 312, ReviewsStartedInWindow: 9),
            Now);

        Assert.Equal(ReviewAdmissionOutcome.Refuse, decision.Outcome);
    }

    [Fact]
    public void Evaluate_LeavesADimensionAloneWhenItWasNotMeasured()
    {
        var decision = ReviewAdmissionEvaluator.Evaluate(
            new ReviewAdmissionPolicy(MaxDiffBytes: 1),
            new ReviewAdmissionMeasurement(ChangedFiles: 3),
            Now);

        Assert.True(decision.IsAdmitted);
    }
}
