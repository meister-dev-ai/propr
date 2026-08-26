// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The one license document an installation has activated.
///     <para>
///         The row exists only while a license is activated, so an installation that has never activated one
///         has no row at all rather than a row holding an empty document.
///     </para>
/// </summary>
public sealed class InstallationLicenseRecord
{
    /// <summary>Fixed key. An installation holds one license.</summary>
    public int Id { get; set; }

    /// <summary>
    ///     The license document wrapped by the secret-protection codec. The document is signed and carries no
    ///     secret of its own, but it is what an installation's entitlement rests on, so it is stored the same
    ///     way as the other values the product must not hand out in readable form.
    /// </summary>
    public string ProtectedToken { get; set; } = string.Empty;

    /// <summary>When this document was stored.</summary>
    public DateTimeOffset ActivatedAt { get; set; }

    /// <summary>Who stored it, when the activation was made by a signed-in user.</summary>
    public Guid? ActivatedByUserId { get; set; }
}
