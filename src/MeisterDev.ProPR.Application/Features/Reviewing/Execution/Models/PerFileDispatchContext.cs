// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Shared dispatch-state container for the standard Reviewing per-file execution shape.</summary>
public sealed record PerFileDispatchContext(
    ReviewJob Job,
    PullRequest PullRequest,
    ReviewSystemContext BaseReviewContext,
    IReadOnlyDictionary<string, ReviewFileResult> ExistingResults,
    IReadOnlyList<ChangedFile> FilesToReview,
    IReadOnlyList<ChangedFile> OrderedFiles,
    IReadOnlyList<Exception> Exceptions);
