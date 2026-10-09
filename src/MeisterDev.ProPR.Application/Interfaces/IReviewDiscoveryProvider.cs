// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Discovers review requests that are candidates for manual or automatic processing.</summary>
public interface IReviewDiscoveryProvider
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Lists open review requests that are candidates for processing.</summary>
    /// <param name="clientId">Owning client.</param>
    /// <param name="repository">Repository to inspect.</param>
    /// <param name="reviewer">Optional requested-reviewer filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="context">Validated manual connection and saved scope; supplied contexts prohibit credential fallback.</param>
    Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null);
}
