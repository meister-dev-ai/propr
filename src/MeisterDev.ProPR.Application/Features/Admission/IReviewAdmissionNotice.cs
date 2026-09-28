// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Admission;

/// <summary>
///     Posts the notice for a review that review admission refused. A refused review never called a model, so
///     there is no summary to publish and nothing carries the reason to the author unless this does.
/// </summary>
public interface IReviewAdmissionNotice
{
    /// <summary>
    ///     Posts <paramref name="reason" /> on the pull request as a review result without findings. The
    ///     client's comment-posting setting governs it like every other comment.
    /// </summary>
    /// <param name="job">The refused job.</param>
    /// <param name="reason">Why the review was refused, and what the author can do about it.</param>
    /// <param name="existingThreads">
    ///     The threads already on the pull request, where the refusing path had them. A provider that keeps one
    ///     summary thread per pull request replies inside the one it already wrote instead of adding a second.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task PostAsync(
        ReviewJob job,
        string reason,
        IReadOnlyList<PrCommentThread>? existingThreads = null,
        CancellationToken ct = default);
}
