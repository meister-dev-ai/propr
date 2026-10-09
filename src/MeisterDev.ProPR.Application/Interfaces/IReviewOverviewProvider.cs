// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Reads bounded pull request metadata using the explicitly selected connection.</summary>
public interface IReviewOverviewProvider
{
    /// <summary>Gets the supported provider family.</summary>
    ScmProvider Provider { get; }

    /// <summary>Reads all-author message totals and provider-native discussion resolution counts.</summary>
    /// <param name="clientId">Owning client identifier.</param>
    /// <param name="review">Configured repository and pull request coordinates.</param>
    /// <param name="context">Selected connection and configured provider scope.</param>
    /// <param name="ct">Caller cancellation token.</param>
    Task<ReviewOverviewDto> GetOverviewAsync(Guid clientId, CodeReviewRef review, ReviewDiscoveryContext context, CancellationToken ct = default);
}
