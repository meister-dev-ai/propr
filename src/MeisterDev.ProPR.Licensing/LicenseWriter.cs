// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     Produces a signed license document. Only the offline issuing tool calls this; the product verifies,
///     it does not sign.
/// </summary>
public static class LicenseWriter
{
    /// <summary>
    ///     Signs claims into the compact form
    ///     <c>base64url(header).base64url(payload).base64url(signature)</c>, base64url without padding.
    /// </summary>
    /// <param name="claims">The payload to sign.</param>
    /// <param name="signingKey">The P-256 private key that signs.</param>
    /// <param name="signingChain">
    ///     The certificates to place in the header's <c>x5c</c> parameter, signing certificate first. A
    ///     trailing root may be included; readers carry it through and ignore it.
    /// </param>
    /// <returns>The compact license document.</returns>
    /// <exception cref="ArgumentException">A claim or the chain is not usable.</exception>
    public static string Sign(LicenseClaims claims, ECDsa signingKey, IReadOnlyList<X509Certificate2> signingChain)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(signingChain);
        Validate(claims, signingKey, signingChain);

        var header = Base64Url.EncodeToString(WriteHeader(signingChain));
        var payload = Base64Url.EncodeToString(WritePayload(claims));
        var signedText = string.Concat(header, ".", payload);

        // The signature covers the ASCII bytes of the two encoded segments and the separator, which is what a
        // reader can reconstruct from the document without re-serializing any JSON.
        var signature = signingKey.SignData(
            Encoding.ASCII.GetBytes(signedText),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var compactLicense = string.Concat(signedText, ".", Base64Url.EncodeToString(signature));

        // A reader refuses a document past this length before it looks at anything else, so bounding each
        // claim on its own is not enough: a long capability list or a deep chain composes into a document
        // every installation refuses as malformed, with a diagnostic that names the file rather than the
        // issuance that produced it. The check here keeps that document from reaching a customer.
        if (compactLicense.Length > LicenseDocument.MaximumLength)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The claims and the certificate chain compose into a document of {compactLicense.Length} characters, "
                    + $"longer than the {LicenseDocument.MaximumLength} a reader accepts."));
        }

        return compactLicense;
    }

    private static void Validate(LicenseClaims claims, ECDsa signingKey, IReadOnlyList<X509Certificate2> signingChain)
    {
        // The format allows one algorithm, so both the signing key and the certificate that names it have to
        // identify that exact curve. Refusing at issuance keeps an unusable document from reaching a customer.
        if (!LicenseClaimValidation.IsNistP256(signingKey))
        {
            throw new ArgumentException("The signing key must be an ECDSA P-256 key, which ES256 requires.", nameof(signingKey));
        }

        if (!LicenseClaimValidation.IsCurrentSchemaVersion(claims.SchemaVersion))
        {
            throw new ArgumentException($"The schema version must be {LicenseDocument.SchemaVersion}.", nameof(claims));
        }

        if (!LicenseClaimValidation.IsBoundedControlFreeText(claims.LicenseId, LicenseClaimValidation.MaximumLicenseIdLength))
        {
            throw new ArgumentException("The license identifier must be non-empty, bounded, and free of control characters.", nameof(claims));
        }

        if (!LicenseClaimValidation.IsBoundedControlFreeText(claims.Licensee, LicenseClaimValidation.MaximumLicenseeLength))
        {
            throw new ArgumentException("The licensee must be non-empty, bounded, and free of control characters.", nameof(claims));
        }

        // The document carries the term at whole-second precision and the reader re-applies the same rules
        // to the written seconds, so the checks run on the serialized values. A term that orders only
        // through sub-second parts would pass here and then be refused by every reader.
        if (!LicenseClaimValidation.HasSupportedIssuedAt(WholeSeconds(claims.IssuedAt)))
        {
            throw new ArgumentException(
                "The issue instant must fall between the unix epoch and the latest representable instant, because the "
                + "certificate path is evaluated at it.",
                nameof(claims));
        }

        // The issue instant is not ordered against the term. A license signed after the term it covers has
        // begun is an ordinary case, so only the term's own bounds are checked here.
        if (!LicenseClaimValidation.HasSupportedTerm(WholeSeconds(claims.NotBefore), WholeSeconds(claims.ExpiresAt)))
        {
            throw new ArgumentException(
                "The license term must begin before it ends and leave room for the grace window.",
                nameof(claims));
        }

        if (claims.Capabilities is null)
        {
            throw new ArgumentException("The capabilities collection must be present.", nameof(claims));
        }

        // A capability name reaches the product's capability set, so it is held to the same rule as the other
        // text claims rather than only to being non-blank.
        if (claims.Capabilities.Any(capability =>
                !LicenseClaimValidation.IsBoundedControlFreeText(capability, LicenseClaimValidation.MaximumCapabilityLength)))
        {
            throw new ArgumentException(
                "A capability must be a non-empty, bounded name free of control characters.",
                nameof(claims));
        }

        if (claims.Limits is null)
        {
            throw new ArgumentException("The limits collection must be present.", nameof(claims));
        }

        // Every element is read when the header is written, not only the first, so an absent certificate
        // anywhere in the chain is refused here rather than raised from serialization.
        if (signingChain.Count == 0 || signingChain.Any(certificate => certificate is null))
        {
            throw new ArgumentException(
                "The header must carry at least the signing certificate, and every certificate in the chain must be present.",
                nameof(signingChain));
        }

        using var certificateKey = ReadPublicKey(signingChain[0]);

        if (certificateKey is null || !LicenseClaimValidation.IsNistP256(certificateKey))
        {
            throw new ArgumentException("The signing certificate must carry an ECDSA P-256 public key.", nameof(signingChain));
        }

        if (!LicenseClaimValidation.HasSamePublicKey(signingKey, certificateKey))
        {
            throw new ArgumentException("The signing key must match the signing certificate's public key.", nameof(signingKey));
        }

        // A reader evaluates the certificate path at the instant the document states as its issue time. An
        // instant outside the signing certificate's own window therefore produces a document refused everywhere
        // as coming from an untrusted signer, with a diagnostic that sends the operator after the signer rather
        // than after the instant that caused it.
        var issuedAt = WholeSeconds(claims.IssuedAt).UtcDateTime;

        if (issuedAt < signingChain[0].NotBefore.ToUniversalTime() || issuedAt > signingChain[0].NotAfter.ToUniversalTime())
        {
            throw new ArgumentException(
                "The issue instant must fall inside the signing certificate's validity window, because the certificate "
                + "path is evaluated at it.",
                nameof(claims));
        }
    }

    private static ECDsa? ReadPublicKey(X509Certificate2 certificate)
    {
        try
        {
            return certificate.GetECDsaPublicKey();
        }
        catch (CryptographicException)
        {
            // A certificate naming a curve the platform cannot construct raises here instead of returning
            // nothing. The method reports an unusable argument as ArgumentException, so the certificate is
            // treated as one carrying no usable key.
            return null;
        }
    }

    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static byte[] WriteHeader(IReadOnlyList<X509Certificate2> signingChain)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString(LicenseJsonNames.Algorithm, LicenseDocument.Algorithm);
            writer.WriteString(LicenseJsonNames.Type, LicenseDocument.Type);
            writer.WriteStartArray(LicenseJsonNames.CertificateChain);

            foreach (var certificate in signingChain)
            {
                writer.WriteStringValue(Convert.ToBase64String(certificate.RawData));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] WritePayload(LicenseClaims claims)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber(LicenseJsonNames.SchemaVersion, claims.SchemaVersion);
            writer.WriteString(LicenseJsonNames.LicenseId, claims.LicenseId);
            writer.WriteString(LicenseJsonNames.Licensee, claims.Licensee);
            writer.WriteNumber(LicenseJsonNames.IssuedAt, claims.IssuedAt.ToUnixTimeSeconds());
            writer.WriteNumber(LicenseJsonNames.NotBefore, claims.NotBefore.ToUnixTimeSeconds());
            writer.WriteNumber(LicenseJsonNames.ExpiresAt, claims.ExpiresAt.ToUnixTimeSeconds());

            writer.WriteStartArray(LicenseJsonNames.Capabilities);

            foreach (var capability in claims.Capabilities)
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();

            // An object with no stated member carries no more meaning than an omitted object, so it is left
            // out and readers see the same all-absent limits either way.
            if (!claims.Limits.IsEmpty)
            {
                writer.WriteStartObject(LicenseJsonNames.Limits);
                WriteLimit(writer, LicenseJsonNames.AuthorsPerMonth, claims.Limits.AuthorsPerMonth);
                WriteLimit(writer, LicenseJsonNames.Clients, claims.Limits.Clients);
                WriteLimit(writer, LicenseJsonNames.Runners, claims.Limits.Runners);
                WriteLimit(writer, LicenseJsonNames.ConcurrentReviews, claims.Limits.ConcurrentReviews);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static void WriteLimit(Utf8JsonWriter writer, string name, LicenseLimit limit)
    {
        if (limit.IsAbsent)
        {
            return;
        }

        if (limit.IsUnlimited)
        {
            writer.WriteString(name, LicenseJsonNames.Unlimited);

            return;
        }

        writer.WriteNumber(name, limit.Count);
    }

    // The relaxed encoder leaves certificate base64 and non-ASCII licensee names readable in the payload.
    // The document is never rendered as HTML, so the escaping the default encoder adds for that case buys
    // nothing here.
    private static JsonWriterOptions WriterOptions => new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };
}
