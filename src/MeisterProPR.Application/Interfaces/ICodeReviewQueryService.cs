// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Fetches provider-neutral review metadata for one review request.</summary>
public interface ICodeReviewQueryService
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Loads normalized metadata for one provider-native review request.</summary>
    Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default);

    /// <summary>Resolves the latest normalized revision for one provider-native review request.</summary>
    Task<ReviewRevision?> GetLatestRevisionAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default);
}
