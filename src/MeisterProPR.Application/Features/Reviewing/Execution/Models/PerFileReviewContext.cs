// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>File-scoped execution state used by Reviewing pipeline stages.</summary>
public sealed record PerFileReviewContext(
    ReviewJob Job,
    ChangedFile ChangedFile,
    ReviewFileResult? FileResult,
    ReviewSystemContext FileReviewContext,
    Guid? ProtocolId,
    object? PerFileArtifacts,
    ReviewResult? ReviewResult);
