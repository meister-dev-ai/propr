// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Where a resolved ceiling came from. A refusal quotes it, so an operator is told whether the number
///     comes from the license on file or from what every installation gets without one.
/// </summary>
public enum LicenseLimitSource
{
    /// <summary>The license in force states this limit, and its value is the ceiling.</summary>
    License = 1,

    /// <summary>
    ///     The community value is the ceiling: no license is in force, the license in force leaves this limit
    ///     out, or a capability the licensed value depends on is not available.
    /// </summary>
    Community = 2,
}
