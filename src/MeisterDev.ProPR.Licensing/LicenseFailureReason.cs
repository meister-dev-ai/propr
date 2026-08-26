// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     Why a license was not accepted. The reasons are separate because they point at different things to
///     check: a malformed file at the file itself, a schema version this build does not read at the build,
///     an expired license at the term.
///     <para>
///         None of them says the document is genuine. A reason is reported as soon as the check that produced
///         it fails, and the checks that establish who issued the document do not all run first, so a reason
///         states what stopped verification rather than what the document is.
///     </para>
/// </summary>
public enum LicenseFailureReason
{
    /// <summary>
    ///     The document is not a well-formed license: the compact form, the header, the certificates, or the
    ///     payload did not hold up. The signature is not part of this reason.
    /// </summary>
    Malformed = 1,

    /// <summary>
    ///     The payload declares a schema version this build does not read. The document may be intact; the
    ///     reader is the older side.
    /// </summary>
    UnsupportedSchemaVersion = 2,

    /// <summary>
    ///     The document was not issued by a signer this build accepts, either because the signing certificate
    ///     does not chain to a trust anchor or because the signature does not hold under that signer's key. The
    ///     two are one reason because they lead to the same conclusion about who wrote the document.
    /// </summary>
    UntrustedSigner = 3,

    /// <summary>The license term has ended.</summary>
    Expired = 4,

    /// <summary>The license term has not started.</summary>
    NotYetValid = 5,

    /// <summary>The build ships no trust anchor, so no license can be verified against it.</summary>
    NoAnchorInThisBuild = 6,
}
