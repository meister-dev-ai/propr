// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

namespace MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;

/// <summary>
///     Query for retrieving the unified diff that was reviewed for a single file result on a review job.
///     The diff is re-fetched on demand from the source control provider using the job's stored coordinates.
/// </summary>
/// <param name="JobId">The review job identifier.</param>
/// <param name="FileResultId">The file result identifier whose file diff should be returned.</param>
public sealed record GetFileDiffQuery(Guid JobId, Guid FileResultId);
