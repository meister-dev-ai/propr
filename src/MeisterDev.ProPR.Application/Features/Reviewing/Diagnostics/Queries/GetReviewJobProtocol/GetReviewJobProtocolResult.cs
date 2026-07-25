// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

/// <summary>
///     Protocol history read model for a single review job.
/// </summary>
public sealed record GetReviewJobProtocolResult(Guid ClientId, IReadOnlyList<ReviewJobProtocolDto> Protocols);
