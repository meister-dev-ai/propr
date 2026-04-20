// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

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
        return diagnosticsReader.GetJobProtocolAsync(query.JobId, ct);
    }
}
