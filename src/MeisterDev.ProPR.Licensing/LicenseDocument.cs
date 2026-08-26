// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The fixed parts of the license document format.
/// </summary>
public static class LicenseDocument
{
    /// <summary>
    ///     The post-term interval an accepted document can remain entitled for. A document whose expiry cannot
    ///     represent the end of this interval is refused before lifecycle state is derived from it.
    /// </summary>
    public static readonly TimeSpan GraceWindow = TimeSpan.FromDays(14);

    /// <summary>
    ///     The only signature algorithm the format allows. A document that declares anything else, including
    ///     <c>none</c>, is refused as malformed before any cryptography runs, so the document cannot select
    ///     how it is checked.
    /// </summary>
    public const string Algorithm = "ES256";

    /// <summary>The value written to the header's <c>typ</c> parameter.</summary>
    public const string Type = "propr-license+jws";

    /// <summary>
    ///     The longest compact document the format allows. A license carries a payload and two certificates,
    ///     which puts a real document in the low kilobytes. A reader refuses anything longer before looking at
    ///     the document at all, which bounds the work an unauthenticated document can cause, and issuance is
    ///     held to the same ceiling so a document past it is refused before it reaches an installation.
    /// </summary>
    public const int MaximumLength = 64 * 1024;

    /// <summary>
    ///     The payload schema version this build reads. A document declaring a higher version is refused with
    ///     its own reason rather than as a signature or format error, because the document may well be valid
    ///     and the reader is the part that is out of date.
    /// </summary>
    public const int SchemaVersion = 1;
}
