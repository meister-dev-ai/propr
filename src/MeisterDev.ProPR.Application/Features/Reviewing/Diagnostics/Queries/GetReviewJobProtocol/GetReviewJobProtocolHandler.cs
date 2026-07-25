// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

/// <summary>
///     Handles Reviewing diagnostics protocol queries.
/// </summary>
public sealed class GetReviewJobProtocolHandler(IReviewDiagnosticsReader diagnosticsReader)
{
    /// <summary>
    ///     Returns the protocol history for the requested review job.
    /// </summary>
    public Task<GetReviewJobProtocolResult?> HandleAsync(
        GetReviewJobProtocolQuery query,
        CancellationToken ct = default)
    {
        return diagnosticsReader.GetJobProtocolAsync(query.JobId, query.IncludeEvents, ct);
    }
}
