// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What kind of ceiling a quota is held to once the license and the community values have been read
///     together.
///     <para>
///         This is the enforcement side of a limit, and it is not the same set of cases as
///         <c>LicenseLimitAllowance</c>, which reports what a license document states. A resolved
///         ceiling is never absent, because a limit the license leaves out falls back to the community value,
///         and it adds the unmetered case, which is a dimension nothing counts rather than one counted against
///         no ceiling.
///     </para>
/// </summary>
public enum LicenseLimitCeiling
{
    /// <summary>The quota has no ceiling.</summary>
    Unlimited = 1,

    /// <summary>The quota is held to a ceiling, carried alongside as the count. It may be zero.</summary>
    Count = 2,

    /// <summary>
    ///     Nothing counts this dimension, so it has no ceiling to enforce. Distinct from
    ///     <see cref="Unlimited" />: that one is a measured dimension a decision may still be made against.
    /// </summary>
    Unmetered = 3,
}
