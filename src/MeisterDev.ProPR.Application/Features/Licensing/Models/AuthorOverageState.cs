// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     How the current calendar month's counted authors stand against the number the license states for them.
///     <para>
///         The comparison is reported rather than acted on. Nothing decides whether work runs from it, and an
///         installation above the number keeps every capability its license grants.
///     </para>
/// </summary>
/// <param name="LicensedCount">The number the license document states for authors within one calendar month.</param>
/// <param name="ObservedCount">
///     The distinct authors the current UTC month holds, automation identities left out.
/// </param>
/// <param name="IsInOverage">Whether the observed count is above the licensed number.</param>
public sealed record AuthorOverageState(long LicensedCount, long ObservedCount, bool IsInOverage);
