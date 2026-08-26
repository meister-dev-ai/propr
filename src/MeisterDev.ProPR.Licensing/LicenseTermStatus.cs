// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     Where a license stands against its own term at the instant it was checked.
///     <para>
///         A license outside its term is still a license whose origin this build established, and the response
///         to that differs by case: an installation may refuse to activate a license whose term has not begun
///         and keep running for a period after one has ended. The verifier therefore states the term and leaves
///         the response to the code that consumes a verified license.
///     </para>
/// </summary>
public enum LicenseTermStatus
{
    /// <summary>The check instant is before the license's <c>nbf</c> claim.</summary>
    NotYetValid = 1,

    /// <summary>The check instant is at or after <c>nbf</c> and before <c>exp</c>.</summary>
    Active = 2,

    /// <summary>The check instant is at or after the license's <c>exp</c> claim.</summary>
    Expired = 3,
}
