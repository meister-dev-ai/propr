// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Admission.Models;

/// <summary>
///     What a review was measured at, in the dimensions a client can bound. A null value means the dimension
///     was not measured at this point, and the bound on it is left for the point that can measure it.
/// </summary>
/// <param name="ChangedFiles">
///     Changed files this review would take on, after exclusions and after the files carried forward from an
///     earlier review are removed.
/// </param>
/// <param name="ChangedLines">Added and removed lines across those files.</param>
/// <param name="DiffBytes">Total size of the unified diffs of those files.</param>
/// <param name="ReviewsStartedInWindow">
///     Reviews of the same pull request submitted within the last hour that ProPR ran: a review still
///     processing, and a review whose token aggregates record a model call.
/// </param>
/// <param name="OldestReviewSubmittedInWindow">
///     When the oldest of those reviews was submitted. A hold lasts until that review leaves the window, so
///     without it a job waits out a whole hour where a minute would do.
/// </param>
public sealed record ReviewAdmissionMeasurement(
    int? ChangedFiles = null,
    int? ChangedLines = null,
    long? DiffBytes = null,
    int? ReviewsStartedInWindow = null,
    DateTimeOffset? OldestReviewSubmittedInWindow = null);
