// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     Configuration for the review-job execution lease and its heartbeat.
///     Bound from environment variables; validated on application startup.
/// </summary>
public sealed class ReviewLeaseOptions : IValidatableObject
{
    /// <summary>
    ///     How many heartbeat intervals must fit inside a lease duration. A lease that is only one or two
    ///     intervals long is lost by a single delayed renewal, which would hand healthy jobs to another host.
    /// </summary>
    public const int MinimumHeartbeatsPerLease = 3;

    /// <summary>
    ///     Seconds a claim grants the lease for before it must be renewed. Also how long an abandoned job
    ///     waits before another party may reclaim it.
    ///     Bound to <c>REVIEW_LEASE_DURATION_SECONDS</c>.
    /// </summary>
    [Range(30, 3600, ErrorMessage = "LeaseDurationSeconds must be between 30 and 3600.")]
    public int LeaseDurationSeconds { get; set; } = 120;

    /// <summary>
    ///     Seconds between lease renewals. Renewal runs on its own schedule rather than at pipeline
    ///     checkpoints, so one long AI or tool call cannot starve it.
    ///     Bound to <c>REVIEW_LEASE_HEARTBEAT_INTERVAL_SECONDS</c>.
    /// </summary>
    [Range(5, 1200, ErrorMessage = "HeartbeatIntervalSeconds must be between 5 and 1200.")]
    public int HeartbeatIntervalSeconds { get; set; } = 20;

    /// <summary>
    ///     Fraction of the interval by which each renewal is randomly brought forward, so a fleet restarted
    ///     together does not settle into one synchronised burst against the database.
    ///     Bound to <c>REVIEW_LEASE_HEARTBEAT_JITTER_FRACTION</c>.
    /// </summary>
    [Range(0d, 0.5d, ErrorMessage = "HeartbeatJitterFraction must be between 0 and 0.5.")]
    public double HeartbeatJitterFraction { get; set; } = 0.2d;

    /// <summary>
    ///     How many consecutive renewal failures are tolerated before the holder stops working on the job.
    ///     Continuing past this point means working without a valid lease, which risks two parties reviewing
    ///     the same job.
    ///     Bound to <c>REVIEW_LEASE_MAX_HEARTBEAT_FAILURES</c>.
    /// </summary>
    [Range(1, 20, ErrorMessage = "MaxConsecutiveHeartbeatFailures must be between 1 and 20.")]
    public int MaxConsecutiveHeartbeatFailures { get; set; } = 3;

    /// <summary>
    ///     Maximum number of claim candidates read per poll cycle. Bounds the read a deep queue costs.
    ///     Bound to <c>REVIEW_LEASE_CLAIM_CANDIDATE_LIMIT</c>.
    /// </summary>
    [Range(1, 500, ErrorMessage = "ClaimCandidateLimit must be between 1 and 500.")]
    public int ClaimCandidateLimit { get; set; } = 50;

    /// <summary>
    ///     How many times a job may be taken back after its lease expired without completing further files.
    ///     Automatic reclaim replaces a deliberate operator restart, so it needs a bound of its own: a job
    ///     that fails the same way every attempt would otherwise cycle at full AI cost forever.
    ///     Bound to <c>REVIEW_LEASE_MAX_CONSECUTIVE_RECLAIMS</c>.
    /// </summary>
    [Range(1, 50, ErrorMessage = "MaxConsecutiveReclaims must be between 1 and 50.")]
    public int MaxConsecutiveReclaims { get; set; } = 3;

    /// <summary>
    ///     How many times a job may be taken back in total. Completing new per-file work clears the
    ///     consecutive count, so this is what stops a job that finishes exactly one file per attempt and then
    ///     crashes from running forever.
    ///     Bound to <c>REVIEW_LEASE_MAX_TOTAL_RECLAIMS</c>.
    /// </summary>
    [Range(1, 500, ErrorMessage = "MaxTotalReclaims must be between 1 and 500.")]
    public int MaxTotalReclaims { get; set; } = 12;

    /// <summary>
    ///     How long after being reclaimed a job is left alone before it may be reclaimed again. After a
    ///     control-plane outage every lease expires at once, and without this the fleet would storm.
    ///     Bound to <c>REVIEW_LEASE_RECLAIM_BACKOFF_SECONDS</c>.
    /// </summary>
    [Range(0, 3600, ErrorMessage = "ReclaimBackoffSeconds must be between 0 and 3600.")]
    public int ReclaimBackoffSeconds { get; set; } = 60;

    /// <summary>
    ///     How many jobs one reclaim sweep takes back. Bounds the burst after a mass expiry.
    ///     Bound to <c>REVIEW_LEASE_MAX_RECLAIMS_PER_SWEEP</c>.
    /// </summary>
    [Range(1, 500, ErrorMessage = "MaxReclaimsPerSweep must be between 1 and 500.")]
    public int MaxReclaimsPerSweep { get; set; } = 20;

    /// <summary>
    ///     Seconds between reclaim sweeps.
    ///     Bound to <c>REVIEW_LEASE_RECLAIM_SWEEP_INTERVAL_SECONDS</c>.
    /// </summary>
    [Range(5, 3600, ErrorMessage = "ReclaimSweepIntervalSeconds must be between 5 and 3600.")]
    public int ReclaimSweepIntervalSeconds { get; set; } = 30;

    /// <summary>
    ///     How long publication may run before the job counts as stuck. Longer than a lease on purpose:
    ///     while comments are going out, taking the job back would risk posting the same review twice.
    ///     Bound to <c>REVIEW_LEASE_PUBLICATION_TIMEOUT_MINUTES</c>.
    /// </summary>
    [Range(1, 720, ErrorMessage = "PublicationTimeoutMinutes must be between 1 and 720.")]
    public int PublicationTimeoutMinutes { get; set; } = 30;

    /// <summary>
    ///     The base URL this replica advertises to runners for job-scoped calls, when the installation runs
    ///     more than one control-plane replica. The workspace mirror is this replica's local disk and the
    ///     per-lease registries are this replica's process, so a runner must reach the replica that granted
    ///     its lease directly — through a load balancer it reaches whichever replica is next, which is
    ///     exactly the wrong one. Unset on a single-replica installation.
    ///     Bound to <c>RUNNER_ADVERTISED_URL</c>.
    /// </summary>
    public string? AdvertisedRunnerUrl { get; set; }

    /// <summary>The lease duration as a <see cref="TimeSpan" />.</summary>
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(this.LeaseDurationSeconds);

    /// <summary>The reclaim backoff as a <see cref="TimeSpan" />.</summary>
    public TimeSpan ReclaimBackoff => TimeSpan.FromSeconds(this.ReclaimBackoffSeconds);

    /// <summary>The reclaim sweep interval as a <see cref="TimeSpan" />.</summary>
    public TimeSpan ReclaimSweepInterval => TimeSpan.FromSeconds(this.ReclaimSweepIntervalSeconds);

    /// <summary>The publication timeout as a <see cref="TimeSpan" />.</summary>
    public TimeSpan PublicationTimeout => TimeSpan.FromMinutes(this.PublicationTimeoutMinutes);

    /// <summary>The heartbeat interval as a <see cref="TimeSpan" />.</summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(this.HeartbeatIntervalSeconds);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (this.LeaseDurationSeconds < this.HeartbeatIntervalSeconds * MinimumHeartbeatsPerLease)
        {
            yield return new ValidationResult(
                $"LeaseDurationSeconds must be at least {MinimumHeartbeatsPerLease} times "
                + "HeartbeatIntervalSeconds so a single late renewal cannot lose a healthy lease.",
                [nameof(this.LeaseDurationSeconds), nameof(this.HeartbeatIntervalSeconds)]);
        }

        // The same rule the runner enforces on its configured URL: the credential rides on every call, so
        // an advertised address must be https unless it is loopback. Refused at startup rather than at the
        // first lease, because a misconfigured replica would otherwise poison every job it grants.
        if (!string.IsNullOrWhiteSpace(this.AdvertisedRunnerUrl))
        {
            // The scheme check is not redundant with the absolute-URI check: on Unix a bare path parses
            // as an absolute file:// URI with no host, which then counts as loopback and slips past the
            // https rule below.
            if (!Uri.TryCreate(this.AdvertisedRunnerUrl, UriKind.Absolute, out var advertised)
                || (!string.Equals(advertised.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(advertised.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                yield return new ValidationResult(
                    "AdvertisedRunnerUrl (RUNNER_ADVERTISED_URL) must be an absolute http or https URL.",
                    [nameof(this.AdvertisedRunnerUrl)]);
            }
            else if (!advertised.IsLoopback && !string.Equals(advertised.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                yield return new ValidationResult(
                    "AdvertisedRunnerUrl (RUNNER_ADVERTISED_URL) must be an https URL. Runners send their "
                    + "credential on every call to it. Loopback addresses are exempt.",
                    [nameof(this.AdvertisedRunnerUrl)]);
            }
        }
    }
}
