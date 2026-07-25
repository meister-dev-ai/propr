// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>File-scoped execution state used by Reviewing pipeline stages.</summary>
public sealed record PerFileReviewContext(
    ReviewJob Job,
    ChangedFile ChangedFile,
    ReviewFileResult? FileResult,
    ReviewSystemContext FileReviewContext,
    Guid? ProtocolId,
    object? PerFileArtifacts,
    ReviewResult? ReviewResult);
