// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Admission.Models;

namespace MeisterDev.ProPR.Application.Features.Admission;

/// <summary>
///     Decides whether a review may start, given the bounds its client set and what the review was measured at.
///     A dimension binds only when both the bound and the measurement are present, so a point that cannot
///     measure a dimension leaves it to the point that can.
/// </summary>
/// <remarks>
///     The size dimensions refuse, because the pull request is too large now and will still be too large on the
///     next attempt. The reviews-per-hour dimension holds, because the same pull request becomes admissible
///     again once the window passes, and refusing a push burst would turn a series of pushes into a series of
///     refusal comments.
/// </remarks>
public static class ReviewAdmissionEvaluator
{
    /// <summary>The window the reviews-per-pull-request bound is counted over.</summary>
    public static TimeSpan BurstWindow { get; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     Returns what to do with a review measured at <paramref name="measurement" /> under
    ///     <paramref name="policy" />.
    /// </summary>
    /// <param name="policy">The client's bounds.</param>
    /// <param name="measurement">What the review was measured at.</param>
    /// <param name="now">The current UTC time, used to date a hold.</param>
    public static ReviewAdmissionDecision Evaluate(
        ReviewAdmissionPolicy policy,
        ReviewAdmissionMeasurement measurement,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(measurement);

        // The size dimensions are evaluated first. A pull request that is too large is refused straight away
        // instead of waiting out a burst window it would fail after anyway.
        if (Exceeds(measurement.ChangedFiles, policy.MaxChangedFiles))
        {
            return ReviewAdmissionDecision.Refuse(
                $"Review not started: {Format(measurement.ChangedFiles!.Value)} changed files exceed the limit of "
                + $"{Format(policy.MaxChangedFiles!.Value)}. Split the pull request or raise the limit.");
        }

        if (Exceeds(measurement.ChangedLines, policy.MaxChangedLines))
        {
            return ReviewAdmissionDecision.Refuse(
                $"Review not started: {Format(measurement.ChangedLines!.Value)} changed lines exceed the limit of "
                + $"{Format(policy.MaxChangedLines!.Value)}. Split the pull request or raise the limit.");
        }

        if (measurement.DiffBytes is { } diffBytes && policy.MaxDiffBytes is { } maxDiffBytes && diffBytes > maxDiffBytes)
        {
            return ReviewAdmissionDecision.Refuse(
                $"Review not started: the diff is {Format(diffBytes)} bytes and exceeds the limit of "
                + $"{Format(maxDiffBytes)} bytes. Split the pull request or raise the limit.");
        }

        if (measurement.ReviewsStartedInWindow is { } started
            && policy.MaxReviewsPerPullRequestPerHour is { } maxPerHour
            && started >= maxPerHour)
        {
            // The window ends when the oldest review in it leaves it, so the hold lasts until that moment and
            // no longer. A caller that reports only a count leaves the whole window to be waited out.
            var windowStart = measurement.OldestReviewSubmittedInWindow ?? now;
            return ReviewAdmissionDecision.Hold(windowStart + BurstWindow, now);
        }

        return ReviewAdmissionDecision.Admit;
    }

    /// <summary>
    ///     Returns the refusal for a repository that passed the client's repository-size bound while it was
    ///     transferred to this host.
    /// </summary>
    /// <param name="measuredMegabytes">The size measured when the transfer was stopped.</param>
    /// <param name="limitMegabytes">The client's bound.</param>
    /// <remarks>
    ///     The bound is enforced where the bytes arrive, not here: nothing measures a repository before it is
    ///     fetched, and a review that fetched it first would already have paid the disk and the time the bound
    ///     exists to save. Workspace preparation reports the two numbers, and this turns them into the words
    ///     the other refusals are written in.
    /// </remarks>
    public static ReviewAdmissionDecision RefuseOversizedRepository(int measuredMegabytes, int limitMegabytes)
    {
        return ReviewAdmissionDecision.Refuse(
            $"Review not started: the repository reached {Format(measuredMegabytes)} MB while it was fetched and "
            + $"exceeds the limit of {Format(limitMegabytes)} MB. Raise the limit to review this repository.");
    }

    private static bool Exceeds(int? measured, int? bound) => measured is { } value && bound is { } limit && value > limit;

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
