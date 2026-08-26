// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     A license whose signer this build accepts: its claims, and where its term stood at the instant it was
///     checked.
///     <para>
///         The certificates the document carried are not part of this type. They establish the signer during
///         verification and are released when it ends, so a consumer holds claims that depend on no disposable
///         resource and can keep them for as long as the installation runs.
///     </para>
/// </summary>
public sealed class VerifiedLicense
{
    internal VerifiedLicense(LicenseClaims claims, LicenseTermStatus termStatus)
    {
        this.Claims = claims;
        this.TermStatus = termStatus;
    }

    /// <summary>The claims the document carries.</summary>
    public LicenseClaims Claims { get; }

    /// <summary>
    ///     Where the license stood against its term at the instant verification was asked about. The instants it
    ///     was decided from are <see cref="LicenseClaims.NotBefore" /> and <see cref="LicenseClaims.ExpiresAt" />,
    ///     so a consumer that has to report a date reads it from the claims.
    /// </summary>
    public LicenseTermStatus TermStatus { get; }
}
