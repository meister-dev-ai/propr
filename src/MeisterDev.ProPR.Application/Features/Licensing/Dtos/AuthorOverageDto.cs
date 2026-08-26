// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Dtos;

/// <summary>
///     Where the current calendar month's counted authors stand against the number the license states for them.
///     <para>
///         Reported for information. The author allowance is recorded and reported rather than enforced, so an
///         installation above the number keeps every capability its license grants and no review, answer or
///         other work is withheld, delayed or degraded because of it.
///     </para>
/// </summary>
/// <param name="LicensedCount">The number the license document states for authors within one calendar month.</param>
/// <param name="ObservedCount">
///     The distinct authors the current UTC month holds, automation identities left out.
/// </param>
/// <param name="IsInOverage">
///     Whether the observed count is above the licensed number. Taken from the current month's comparison, so
///     it reads false again once the count is at or below the number.
/// </param>
public sealed record AuthorOverageDto(long LicensedCount, long ObservedCount, bool IsInOverage);
