// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Diagnostics.CodeAnalysis;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The outcome of verifying a license against the trust anchor a build carries: the verified license, or a
///     typed reason no license was established together with a diagnostic an operator can act on.
///     <para>
///         A term that has not begun or has ended is not one of those reasons. It is carried on the verified
///         license as <see cref="VerifiedLicense.TermStatus" />, so the code that decides what to do about a
///         term still has the claims to decide from.
///     </para>
/// </summary>
public sealed class LicenseVerificationResult
{
    private LicenseVerificationResult(VerifiedLicense? license, LicenseFailureReason? failureReason, string? failureDetail)
    {
        this.License = license;
        this.FailureReason = failureReason;
        this.FailureDetail = failureDetail;
    }

    /// <summary>Whether the license was established as coming from a signer this build accepts.</summary>
    [MemberNotNullWhen(true, nameof(License))]
    [MemberNotNullWhen(false, nameof(FailureReason))]
    [MemberNotNullWhen(false, nameof(FailureDetail))]
    public bool IsVerified => this.License is not null;

    /// <summary>The verified license, when one was established. Nothing here needs disposing.</summary>
    public VerifiedLicense? License { get; }

    /// <summary>Why no license was established, when none was.</summary>
    public LicenseFailureReason? FailureReason { get; }

    /// <summary>
    ///     What stopped verification, in terms an operator can act on. It repeats no text from the document and
    ///     stays bounded in length: at most one number read from the document appears in it, such as the schema
    ///     version the payload declares. It is therefore safe to log.
    /// </summary>
    public string? FailureDetail { get; }

    /// <summary>Builds a verified result.</summary>
    /// <param name="license">The verified license.</param>
    /// <returns>The result.</returns>
    public static LicenseVerificationResult Verified(VerifiedLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);

        return new LicenseVerificationResult(license, null, null);
    }

    /// <summary>Builds a result that established no license.</summary>
    /// <param name="reason">Why no license was established.</param>
    /// <param name="detail">What stopped verification.</param>
    /// <returns>The result.</returns>
    public static LicenseVerificationResult Failure(LicenseFailureReason reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        return new LicenseVerificationResult(null, reason, detail);
    }
}
