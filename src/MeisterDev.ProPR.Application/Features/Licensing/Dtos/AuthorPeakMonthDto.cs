// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>
///     The busiest of the twelve calendar months ending with the current one, and how many authors it held.
/// </summary>
/// <param name="Month">
///     The first day of the month, in UTC. A date rather than an instant, because the unit is the month and no
///     part of the day within it carries meaning.
/// </param>
/// <param name="AuthorCount">Distinct authors the month held, automation identities left out.</param>
public sealed record AuthorPeakMonthDto(DateOnly Month, long AuthorCount);
