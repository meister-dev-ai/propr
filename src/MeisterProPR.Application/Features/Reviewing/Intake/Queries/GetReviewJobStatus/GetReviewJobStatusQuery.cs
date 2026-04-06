// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;

/// <summary>Query for retrieving the status of a submitted review job.</summary>
public sealed record GetReviewJobStatusQuery(Guid JobId);
