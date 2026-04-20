// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Discovers review requests that are candidates for manual or automatic processing.</summary>
public interface IReviewDiscoveryProvider
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Lists open review requests that are candidates for processing.</summary>
    Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default);
}
