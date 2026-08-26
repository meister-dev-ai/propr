// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Why a premium capability is not available, in a form a caller can branch on.
///     <para>
///         The cases are separate because the operator response differs: an installation with no license activates
///         one, an installation whose license does not name the capability needs a license that covers it, an
///         installation whose license ran out renews it, an installation whose license has not started yet waits,
///         and a capability an administrator turned off is turned back on through the override endpoint.
///     </para>
///     <para>
///         Known values are <c>noLicense</c>, <c>notInLicense</c>, <c>disabledByOverride</c>,
///         <c>reverted</c>, and <c>notYetValid</c>.
///     </para>
///     <para>
///         Further cases are added rather than replacing these, so code written against the current set keeps
///         working. A client has to tolerate a value it does not know, because a newer installation can send one.
///     </para>
/// </summary>
public enum PremiumCapabilityUnavailableReason
{
    /// <summary>
    ///     The installation has no license to read a term from: none is on file, the stored value could not be
    ///     read back, or the document did not verify.
    /// </summary>
    NoLicense = 1,

    /// <summary>A license is in force but it does not name this capability.</summary>
    NotInLicense = 2,

    /// <summary>The capability is licensed but an administrator turned it off for this installation.</summary>
    DisabledByOverride = 3,

    /// <summary>The license term ended and the grace window after it ended too, so the installation is back on Community.</summary>
    Reverted = 4,

    /// <summary>A license is on file but its term has not begun, so nothing it grants is available yet.</summary>
    NotYetValid = 5,
}
