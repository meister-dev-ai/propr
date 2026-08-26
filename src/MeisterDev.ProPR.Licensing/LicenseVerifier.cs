// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     Decides whether a license document comes from a signer this build accepts, and where the license stands
///     against its own term.
///     <para>
///         Verification is offline and takes everything it depends on from its inputs. The trust anchor arrives
///         through the constructor and the instant to judge the term by arrives with the call, so this type
///         reads no clock, no file, and no configuration of its own.
///     </para>
///     <para>
///         Two things have to hold: the signature has to verify under the public key of the certificate the
///         document names as its signer, and that certificate has to lead to the trust anchor. Neither alone is
///         enough — a signature only says which key wrote the document, and a certificate path only says which
///         signers this build accepts.
///     </para>
/// </summary>
public sealed class LicenseVerifier
{
    private readonly LicenseTrustAnchor _trustAnchor;

    /// <summary>Creates a verifier over one trust anchor.</summary>
    /// <param name="trustAnchor">
    ///     The anchor every accepted license has to lead back to. The caller keeps ownership and disposes it,
    ///     because one anchor serves every verification an installation performs.
    /// </param>
    public LicenseVerifier(LicenseTrustAnchor trustAnchor)
    {
        ArgumentNullException.ThrowIfNull(trustAnchor);

        this._trustAnchor = trustAnchor;
    }

    /// <summary>
    ///     Verifies a license document.
    ///     <para>
    ///         When more than one thing is wrong, the first of these that applies is reported: the document is
    ///         not three segments carrying a header with a usable signing certificate, its signature does not
    ///         hold under that certificate's key, its payload is not well formed or declares a schema version
    ///         this build does not read, this build carries no trust anchor, or its signer does not lead to
    ///         that anchor. The signature is settled before the payload is read, so a document with a broken
    ///         signature reports an untrusted signer whatever else its payload states, including on a build
    ///         that carries no anchor.
    ///     </para>
    /// </summary>
    /// <param name="compactLicense">The compact license document.</param>
    /// <param name="now">
    ///     The instant to judge the license term by. It is a parameter so the caller decides which clock is
    ///     authoritative; it does not affect whether the document verifies, only the term status it reports.
    /// </param>
    /// <returns>The verified license, or the reason none was established.</returns>
    public LicenseVerificationResult Verify(string compactLicense, DateTimeOffset now)
    {
        ECDsa? signerPublicKey = null;

        try
        {
            // The signature is checked against the key of the certificate the document itself names, before the
            // certificate path is built. Both checks have to hold, so their order does not change which
            // documents verify. It does decide what the claims mean in between: once the signature holds, the
            // payload is the one the holder of that key signed, which makes the issue instant the path is
            // evaluated at a signed value rather than one read out of an unchecked payload.
            var read = LicenseReader.Read(
                compactLicense, chain =>
                {
                    signerPublicKey = chain[0].GetECDsaPublicKey();

                    return signerPublicKey;
                });

            if (!read.IsSuccess)
            {
                return LicenseVerificationResult.Failure(read.FailureReason.Value, read.FailureDetail);
            }

            using var license = read.License;

            // The anchor is looked at after the document has been read, so a build that ships none still tells
            // an operator that a file is malformed rather than reporting the missing anchor for every document.
            if (!this._trustAnchor.IsPresent)
            {
                return LicenseVerificationResult.Failure(
                    LicenseFailureReason.NoAnchorInThisBuild,
                    "This build carries no licensing trust anchor, so it can accept no license.");
            }

            // The certificate path is evaluated at the instant the license was issued, not at the current time,
            // so a license runs for its full term after the certificate that signed it has expired. A document
            // from a signer this build does not accept can state any issue instant; that only moves the instant
            // its own path is evaluated at, and the path still has to terminate at the anchor.
            if (!LicenseChainPolicy.LeadsToAnchor(
                    license.SigningChain,
                    this._trustAnchor.Certificate,
                    license.Claims.IssuedAt,
                    out var chainDetail))
            {
                return LicenseVerificationResult.Failure(LicenseFailureReason.UntrustedSigner, chainDetail);
            }

            return LicenseVerificationResult.Verified(new VerifiedLicense(license.Claims, TermStatusAt(license.Claims, now)));
        }
        finally
        {
            // The reader takes the key from the selector without taking ownership of it.
            signerPublicKey?.Dispose();
        }
    }

    private static LicenseTermStatus TermStatusAt(LicenseClaims claims, DateTimeOffset now)
    {
        if (now < claims.NotBefore)
        {
            return LicenseTermStatus.NotYetValid;
        }

        if (now >= claims.ExpiresAt)
        {
            return LicenseTermStatus.Expired;
        }

        return LicenseTermStatus.Active;
    }
}
