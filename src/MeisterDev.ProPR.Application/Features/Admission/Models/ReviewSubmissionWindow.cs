// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Admission.Models;

/// <summary>
///     What one pull request had reviewed inside the rolling admission window: how many of the reviews
///     submitted in it ProPR ran, and when the oldest of those was submitted. The timestamp turns a hold into
///     a wait of the right length, because the window ends when that review leaves it.
/// </summary>
/// <param name="Count">
///     Reviews submitted for the pull request inside the window that ProPR ran: a review still processing, and
///     a review whose token aggregates record a model call.
/// </param>
/// <param name="OldestSubmittedAt">
///     When the oldest of them was submitted, or <see langword="null" /> when there were none.
/// </param>
public sealed record ReviewSubmissionWindow(int Count, DateTimeOffset? OldestSubmittedAt)
{
    /// <summary>A window no review was submitted in.</summary>
    public static ReviewSubmissionWindow Empty { get; } = new(0, null);
}
