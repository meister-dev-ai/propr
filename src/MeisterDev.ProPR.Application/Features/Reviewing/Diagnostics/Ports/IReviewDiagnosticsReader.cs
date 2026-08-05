// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;

/// <summary>
///     Reviewing-owned read model for review diagnostics and protocol history.
/// </summary>
public interface IReviewDiagnosticsReader
{
    /// <summary>
    ///     Returns the full protocol history for a review job, or <see langword="null" /> when the job does not exist.
    /// </summary>
    Task<GetReviewJobProtocolResult?> GetJobProtocolAsync(
        Guid jobId,
        bool includeEvents = true,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns one protocol pass for a review job, or <see langword="null" /> when the job or protocol does not exist.
    /// </summary>
    Task<ReviewJobProtocolDto?> GetJobProtocolPassAsync(
        Guid jobId,
        Guid protocolId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the protocol history for a thread pass, one record per thread it evaluated, or
    ///     <see langword="null" /> when the pass does not exist.
    /// </summary>
    /// <remarks>
    ///     Shaped as review-job protocols on purpose: an operator inspecting a pull request reads one trace view,
    ///     and the pass answers the same conversation the review used to.
    /// </remarks>
    Task<GetReviewJobProtocolResult?> GetThreadPassProtocolAsync(
        Guid threadPassJobId,
        bool includeEvents = true,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns one protocol pass for a thread pass, or <see langword="null" /> when the pass or protocol does
    ///     not exist.
    /// </summary>
    Task<ReviewJobProtocolDto?> GetThreadPassProtocolPassAsync(
        Guid threadPassJobId,
        Guid protocolId,
        CancellationToken ct = default);
}
