// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when every comment thread in a publishing pass was rejected by the SCM provider, so nothing was
///     posted. Surfaced instead of returning a silently-successful result, so the job is marked failed while the
///     per-thread provider errors remain available for diagnostics.
/// </summary>
public sealed class ReviewCommentPublicationFailedException : AggregateException
{
    /// <summary>
    ///     Initializes a new <see cref="ReviewCommentPublicationFailedException" />.
    /// </summary>
    /// <param name="diagnostics">Diagnostics for the failed pass, carrying every recorded per-thread failure.</param>
    /// <param name="innerExceptions">The provider exceptions for each rejected thread creation.</param>
    public ReviewCommentPublicationFailedException(
        ReviewCommentPostingDiagnosticsDto diagnostics,
        IEnumerable<Exception> innerExceptions)
        : base(
            $"All {diagnostics.FailedCount} comment thread(s) were rejected by the provider; nothing was posted.",
            innerExceptions)
    {
        this.Diagnostics = diagnostics;
    }

    /// <summary>Diagnostics for the failed posting pass, including the per-thread failures with their provider errors.</summary>
    public ReviewCommentPostingDiagnosticsDto Diagnostics { get; }
}
