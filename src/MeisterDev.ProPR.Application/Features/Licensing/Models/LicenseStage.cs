// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Where an installation stands in its license's lifecycle, from the term the license carries and the
///     instant it was judged at.
///     <para>
///         This is the licensing layer's view, and it is coarser than the term alone: it adds a window before
///         the term ends in which the installation is still entitled but the expiry is close enough to act on,
///         and a window after it in which entitlement continues so an expiry does not stop work in progress.
///         <see cref="MeisterDev.ProPR.Licensing.LicenseTermStatus" /> stays the plain reading of
///         <c>nbf</c> and <c>exp</c> that the verifier reports.
///     </para>
///     <para>
///         Known values are <c>none</c>, <c>notYetValid</c>, <c>active</c>, <c>warning</c>, <c>grace</c>, and
///         <c>reverted</c>.
///     </para>
///     <para>
///         Further cases are added rather than replacing these, so code written against the current set keeps
///         working. A client has to tolerate a value it does not know, because a newer installation can send one.
///     </para>
/// </summary>
public enum LicenseStage
{
    /// <summary>
    ///     The installation has no license whose term can be read: none is on file, the stored value could not
    ///     be read back, or the document did not verify.
    /// </summary>
    None = 0,

    /// <summary>The term has not begun. Nothing the license grants is available yet.</summary>
    NotYetValid = 1,

    /// <summary>The term is running and its end is further away than the warning window.</summary>
    Active = 2,

    /// <summary>
    ///     The term is running and ends within the warning window. Everything the license grants stays
    ///     available; the stage exists so an operator can renew before the term ends.
    /// </summary>
    Warning = 3,

    /// <summary>
    ///     The term has ended and the grace window has not. Everything the license grants stays available,
    ///     including the limit values it states, so an expiry does not interrupt work already under way.
    /// </summary>
    Grace = 4,

    /// <summary>
    ///     The term and the grace window have both ended. Commercial capabilities resolve as unavailable.
    ///     Nothing stored is deleted and read paths keep working, so activating a renewed license restores the
    ///     installation to what it was.
    /// </summary>
    Reverted = 5,
}
