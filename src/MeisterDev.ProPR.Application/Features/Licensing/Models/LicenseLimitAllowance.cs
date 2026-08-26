// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What a license states for one limit.
///     <para>
///         The three cases lead to different readings and are kept apart. An absent limit is not constrained by
///         the license at all; an unlimited limit was stated and set to no ceiling; a stated count carries the
///         ceiling, which may be zero.
///     </para>
/// </summary>
public enum LicenseLimitAllowance
{
    /// <summary>The license does not state this limit.</summary>
    Absent = 1,

    /// <summary>The license states this limit and sets no ceiling.</summary>
    Unlimited = 2,

    /// <summary>The license states a ceiling, carried alongside as the licensed count.</summary>
    Count = 3,
}
