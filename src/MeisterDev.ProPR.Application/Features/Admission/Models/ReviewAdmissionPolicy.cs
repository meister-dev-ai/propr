// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Application.Features.Admission.Models;

/// <summary>
///     The size bounds a client puts on a review before it starts. Every bound is optional; a null bound means
///     that dimension is unbounded. The bounds protect an installation from a pull request large enough to
///     exhaust its memory or its month's budget in one run, so they are enforced in every edition and setting
///     them needs no licence.
/// </summary>
/// <param name="MaxChangedFiles">
///     The largest number of changed files a review may take on, counted after exclusions and after files
///     carried forward from a previous review are removed.
/// </param>
/// <param name="MaxChangedLines">The largest number of added and removed lines across those files.</param>
/// <param name="MaxDiffBytes">The largest total size, in bytes, of the unified diffs of those files.</param>
/// <param name="MaxReviewsPerPullRequestPerHour">
///     How many reviews one pull request may start within an hour. A push burst past this holds the job until
///     the window has passed.
/// </param>
/// <param name="MaxRepositoryMegabytes">
///     The largest repository, in mebibytes (1,048,576 bytes), a review may be run against. Workspace
///     preparation watches how large the mirror and the checkout grow while git transfers them and stops the
///     transfer once the bound is passed, so the bound also bounds the disk and the time one review may spend
///     on a repository ProPR has not seen before.
/// </param>
public sealed record ReviewAdmissionPolicy(
    int? MaxChangedFiles = null,
    int? MaxChangedLines = null,
    int? MaxDiffBytes = null,
    int? MaxReviewsPerPullRequestPerHour = null,
    int? MaxRepositoryMegabytes = null)
{
    /// <summary>A policy with no bounds set: nothing is refused and nothing is held.</summary>
    public static ReviewAdmissionPolicy None { get; } = new();

    /// <summary>True when at least one bound is set, so admission has something to evaluate.</summary>
    public bool AnyConfigured =>
        this.MaxChangedFiles is not null ||
        this.MaxChangedLines is not null ||
        this.MaxDiffBytes is not null ||
        this.MaxReviewsPerPullRequestPerHour is not null ||
        this.MaxRepositoryMegabytes is not null;

    /// <summary>
    ///     A stable rendering of every bound, stored on a review this policy refused.
    /// </summary>
    /// <remarks>
    ///     A refused review is the review of its pull request head: an automatic trigger creates no second job
    ///     for the same head, because the measurement would be the same and each attempt transfers the
    ///     repository again. The head is reviewed again once an administrator changes a bound, and the stored
    ///     rendering is what a later trigger compares the client's current bounds with.
    /// </remarks>
    public string Fingerprint => string.Join(
        '/',
        Render(this.MaxChangedFiles),
        Render(this.MaxChangedLines),
        Render(this.MaxDiffBytes),
        Render(this.MaxReviewsPerPullRequestPerHour),
        Render(this.MaxRepositoryMegabytes));

    /// <summary>True when a bound that is measured from the pull request's diff is set.</summary>
    public bool AnyDiffBoundConfigured =>
        this.MaxChangedFiles is not null ||
        this.MaxChangedLines is not null ||
        this.MaxDiffBytes is not null;

    /// <summary>Renders one bound for the fingerprint. A bound that is not set renders as nothing.</summary>
    /// <param name="bound">The bound to render.</param>
    private static string Render(int? bound)
    {
        return bound?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
