// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>The revision a client last engaged with for a pull request, as recorded by that client's review jobs.</summary>
/// <param name="StoredRevisionKey">The persisted revision key the engagement is identified by.</param>
/// <param name="ReviewRevision">
///     The provider-neutral revision reference the job carried, or <see langword="null" /> when it carried none and
///     the iteration id is the whole identity.
/// </param>
/// <param name="IterationId">The iteration the engagement ran at.</param>
public sealed record EngagedReviewRevision(
    string StoredRevisionKey,
    ReviewRevision? ReviewRevision,
    int IterationId);
