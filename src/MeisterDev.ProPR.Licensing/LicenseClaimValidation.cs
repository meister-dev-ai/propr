// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Globalization;
using System.Security.Cryptography;

namespace MeisterDev.ProPR.Licensing;

internal static class LicenseClaimValidation
{
    public const int MaximumLicenseIdLength = 256;
    public const int MaximumLicenseeLength = 512;

    /// <summary>
    ///     The longest capability name a document may carry. A capability name is an identifier the product
    ///     matches against, so the bound is well above every name in use and still keeps an unverified document
    ///     from carrying arbitrary text into the capability set.
    /// </summary>
    public const int MaximumCapabilityLength = 128;

    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    public static readonly DateTimeOffset EarliestIssuedAt = DateTimeOffset.UnixEpoch;

    public static readonly DateTimeOffset LatestIssuedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.MaxValue.ToUnixTimeSeconds());

    /// <summary>
    ///     Whether a text claim is present, within its bound, and free of characters that change the shape of
    ///     the output it is repeated into. The licensee is written to a single line of command output, to
    ///     activation history and to the admin UI, where a line or paragraph separator ends the line the output
    ///     format expects to be one, and a bidi override reverses everything rendered after it. The claims are
    ///     written by the issuing vendor, so this is output hygiene and not a defence against a hostile
    ///     document.
    /// </summary>
    public static bool IsBoundedControlFreeText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(IsDisallowedInClaimText);

    /// <summary>
    ///     Whether a character is one a text claim may not carry. Alongside the control characters this covers
    ///     the format category, which holds the zero-width characters, the byte-order mark and the bidi
    ///     overrides, and the two separator categories, which hold the line and paragraph separators.
    /// </summary>
    private static bool IsDisallowedInClaimText(char character) =>
        char.IsControl(character)
        || char.GetUnicodeCategory(character)
            is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    ///     Whether the term the document states can be held and judged. The issue instant is not part of it:
    ///     a license is often signed after the term it covers has begun, so requiring the signature to precede
    ///     the term would refuse an ordinary back-dated term.
    /// </summary>
    public static bool HasSupportedTerm(DateTimeOffset notBefore, DateTimeOffset expiresAt) =>
        notBefore < expiresAt
        && expiresAt <= DateTimeOffset.MaxValue - LicenseDocument.GraceWindow;

    /// <summary>
    ///     Whether the issue instant can be used as stated. Verification evaluates the certificate path at this
    ///     instant, so it has to be one the chain engine accepts as a verification time.
    /// </summary>
    public static bool HasSupportedIssuedAt(DateTimeOffset issuedAt) =>
        issuedAt >= EarliestIssuedAt && issuedAt <= LatestIssuedAt;

    public static bool IsCurrentSchemaVersion(int schemaVersion) => schemaVersion == LicenseDocument.SchemaVersion;

    public static bool IsNistP256(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            return string.Equals(
                key.ExportParameters(false).Curve.Oid.Value,
                NistP256Oid,
                StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool HasSamePublicKey(ECDsa first, ECDsa second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        try
        {
            var firstParameters = first.ExportParameters(false);
            var secondParameters = second.ExportParameters(false);

            return string.Equals(
                       firstParameters.Curve.Oid.Value,
                       secondParameters.Curve.Oid.Value,
                       StringComparison.Ordinal)
                   && firstParameters.Q.X is { } firstX
                   && firstParameters.Q.Y is { } firstY
                   && secondParameters.Q.X is { } secondX
                   && secondParameters.Q.Y is { } secondY
                   && CryptographicOperations.FixedTimeEquals(firstX, secondX)
                   && CryptographicOperations.FixedTimeEquals(firstY, secondY);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
