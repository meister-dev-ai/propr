// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Result of atomically attempting to add a review job while suppressing active duplicates.</summary>
public sealed record TryAddReviewJobResult(bool WasAdded, ReviewJob? DuplicateJob, int CancelledSupersededJobCount);
