// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Admission.Models;

/// <summary>What review admission decided about one job.</summary>
public enum ReviewAdmissionOutcome
{
    /// <summary>The review may run.</summary>
    Admit = 0,

    /// <summary>The review is refused and ends without a model call. <see cref="ReviewAdmissionDecision.Reason" /> says why.</summary>
    Refuse = 1,

    /// <summary>The review waits until <see cref="ReviewAdmissionDecision.AdmissibleAt" /> and then runs.</summary>
    Hold = 2,
}

/// <summary>
///     The decision review admission reached for one job. A refusal carries the reason in the words posted on
///     the pull request: the measured value, the bound, and what the author can do about it.
/// </summary>
public sealed record ReviewAdmissionDecision
{
    private ReviewAdmissionDecision(ReviewAdmissionOutcome outcome, string? reason, DateTimeOffset? admissibleAt)
    {
        this.Outcome = outcome;
        this.Reason = reason;
        this.AdmissibleAt = admissibleAt;
    }

    /// <summary>What admission decided.</summary>
    public ReviewAdmissionOutcome Outcome { get; }

    /// <summary>The refusal reason, or null when the review was admitted or held.</summary>
    public string? Reason { get; }

    /// <summary>When a held review becomes admissible, or null when it was not held.</summary>
    public DateTimeOffset? AdmissibleAt { get; }

    /// <summary>True when the review may run.</summary>
    public bool IsAdmitted => this.Outcome == ReviewAdmissionOutcome.Admit;

    /// <summary>The review may run.</summary>
    public static ReviewAdmissionDecision Admit { get; } = new(ReviewAdmissionOutcome.Admit, null, null);

    /// <summary>The review is refused, with the reason to record and post.</summary>
    /// <param name="reason">Why the review was refused, and what the author can do about it.</param>
    public static ReviewAdmissionDecision Refuse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ReviewAdmissionDecision(ReviewAdmissionOutcome.Refuse, reason, null);
    }

    /// <summary>
    ///     The review waits until the window has passed. A timestamp that has already elapsed carries no wait,
    ///     so it yields <see cref="Admit" />: a decision holding a past instant would contradict the contract
    ///     that a held review runs once that instant arrives.
    /// </summary>
    /// <param name="admissibleAt">When the review becomes admissible.</param>
    /// <param name="now">The current UTC time the timestamp is weighed against.</param>
    public static ReviewAdmissionDecision Hold(DateTimeOffset admissibleAt, DateTimeOffset now) =>
        admissibleAt <= now ? Admit : new ReviewAdmissionDecision(ReviewAdmissionOutcome.Hold, null, admissibleAt);
}
