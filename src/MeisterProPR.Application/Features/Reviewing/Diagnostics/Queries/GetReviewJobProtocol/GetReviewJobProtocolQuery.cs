// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

/// <summary>
///     Query for retrieving the protocol history of a single review job.
/// </summary>
public sealed record GetReviewJobProtocolQuery(Guid JobId);
