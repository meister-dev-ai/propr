// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Reviewing-owned boundary for per-file comment relevance filtering.
/// </summary>
public interface ICommentRelevanceFilter
{
    /// <summary>Stable identifier for this filter implementation.</summary>
    string ImplementationId { get; }

    /// <summary>Recorded version string for this filter implementation.</summary>
    string ImplementationVersion { get; }

    /// <summary>
    ///     Evaluates the normalized per-file review comments and returns one keep-or-discard decision per input comment.
    /// </summary>
    /// <param name="request">Normalized filter input for a single reviewed file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CommentRelevanceFilterResult> FilterAsync(CommentRelevanceFilterRequest request, CancellationToken ct = default);
}
