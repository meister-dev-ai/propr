// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;

/// <summary>
///     Reviewing-owned read model for review diagnostics and protocol history.
/// </summary>
public interface IReviewDiagnosticsReader
{
    /// <summary>
    ///     Returns the full protocol history for a review job, or <see langword="null" /> when the job does not exist.
    /// </summary>
    Task<GetReviewJobProtocolResult?> GetJobProtocolAsync(Guid jobId, CancellationToken ct = default);
}
