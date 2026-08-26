// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     Reads a compact license document: structure, header, certificates, signature, and claims.
///     <para>
///         Building the certificate chain against a trust anchor is not done here. This type answers whether
///         the document is well formed and whether it was signed by the key it is checked against. Deciding
///         which key that should be is the caller's, through the selector overload of
///         <see cref="Read(string, Func{IReadOnlyList{X509Certificate2}, ECDsa})" />.
///     </para>
/// </summary>
public static class LicenseReader
{
    private static readonly long MinimumUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaximumUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    // A P-256 signature in JOSE form is the fixed-width r and s values concatenated, so its length is known
    // ahead of time and a document of any other length is refused before the key is involved.
    private const int SignatureLength = 64;

    /// <summary>
    ///     Parses a document without checking its signature. The claims are what the document says, not what
    ///     has been verified; call <see cref="ParsedLicense.HasValidSignature" /> once a key is available.
    /// </summary>
    /// <param name="compactLicense">The compact license document.</param>
    /// <returns>The parsed document, or a refusal.</returns>
    public static LicenseReadResult Parse(string compactLicense)
    {
        var envelope = TryReadEnvelope(compactLicense, out var envelopeDetail);

        if (envelope is null)
        {
            return LicenseReadResult.Failure(LicenseFailureReason.Malformed, envelopeDetail);
        }

        return CompleteFromEnvelope(envelope);
    }

    /// <summary>
    ///     Parses a document, lets the caller choose the verification key from the certificates the header
    ///     carries, and checks the signature before reading any claim.
    /// </summary>
    /// <param name="compactLicense">The compact license document.</param>
    /// <param name="keySelector">
    ///     Receives the header's certificates, signing certificate first, and returns the public key to verify
    ///     with, or <see langword="null" /> when the chain does not lead to a signer the installation accepts.
    ///     The selector keeps ownership of the key it returns.
    /// </param>
    /// <returns>The parsed document, or a refusal.</returns>
    public static LicenseReadResult Read(string compactLicense, Func<IReadOnlyList<X509Certificate2>, ECDsa?> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        var envelope = TryReadEnvelope(compactLicense, out var envelopeDetail);

        if (envelope is null)
        {
            return LicenseReadResult.Failure(LicenseFailureReason.Malformed, envelopeDetail);
        }

        try
        {
            var signerPublicKey = keySelector(envelope.SigningChain);

            if (signerPublicKey is null)
            {
                envelope.DisposeChain();

                return LicenseReadResult.Failure(
                    LicenseFailureReason.UntrustedSigner,
                    "The certificates in the header do not lead to a signer this installation accepts.");
            }

            // A document that does not verify under the signer's own key was not issued by that signer, whatever
            // its certificates say, so it is refused for the same reason as a chain that leads nowhere.
            if (!HasValidSignature(envelope, signerPublicKey))
            {
                envelope.DisposeChain();

                return LicenseReadResult.Failure(
                    LicenseFailureReason.UntrustedSigner,
                    "The signature does not cover the header and payload of this document under the selected signer's key.");
            }

            return CompleteFromEnvelope(envelope);
        }
        catch
        {
            envelope.DisposeChain();
            throw;
        }
    }

    /// <summary>
    ///     Parses a document and checks its signature against one key before reading any claim.
    ///     <para>
    ///         The key is the caller's choice and the header's certificates are not consulted, so this form
    ///         suits a caller that already holds the signer's key and tests that supply one directly. A caller
    ///         that has to establish the signer from the document uses the selector overload.
    ///     </para>
    /// </summary>
    /// <param name="compactLicense">The compact license document.</param>
    /// <param name="signerPublicKey">The public key the document is expected to be signed with.</param>
    /// <returns>The parsed document, or a refusal.</returns>
    public static LicenseReadResult Read(string compactLicense, ECDsa signerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(signerPublicKey);

        return Read(compactLicense, _ => signerPublicKey);
    }

    private static LicenseReadResult CompleteFromEnvelope(LicenseEnvelope envelope)
    {
        var claims = TryReadClaims(envelope.PayloadJson, out var reason, out var detail);

        if (claims is null)
        {
            envelope.DisposeChain();

            return LicenseReadResult.Failure(reason, detail);
        }

        return LicenseReadResult.Success(new ParsedLicense(claims, envelope.SigningChain, envelope.SignedBytes, envelope.Signature));
    }

    private static bool HasValidSignature(LicenseEnvelope envelope, ECDsa signerPublicKey)
    {
        try
        {
            return signerPublicKey.VerifyData(
                envelope.SignedBytes,
                envelope.Signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            // A key of the wrong curve or size cannot confirm the document. That outcome is a refusal, not a
            // fault of the caller, so it is reported the same way a signature that does not hold is.
            return false;
        }
    }

    private static LicenseEnvelope? TryReadEnvelope(string compactLicense, out string detail)
    {
        detail = string.Empty;

        if (compactLicense is { Length: > LicenseDocument.MaximumLength })
        {
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"The license is longer than the {LicenseDocument.MaximumLength} characters a license document may occupy.");

            return null;
        }

        if (string.IsNullOrWhiteSpace(compactLicense))
        {
            detail = "The license is empty.";

            return null;
        }

        var firstSeparator = compactLicense.IndexOf('.');
        var secondSeparator = firstSeparator < 0
            ? -1
            : compactLicense.IndexOf('.', firstSeparator + 1);

        if (firstSeparator <= 0
            || secondSeparator <= firstSeparator + 1
            || secondSeparator == compactLicense.Length - 1
            || compactLicense.IndexOf('.', secondSeparator + 1) >= 0)
        {
            detail = "The license must be three dot-separated segments: header, payload, and signature.";

            return null;
        }

        var headerJson = TryDecodeSegment(compactLicense.AsSpan(0, firstSeparator));
        var payloadJson = TryDecodeSegment(compactLicense.AsSpan(firstSeparator + 1, secondSeparator - firstSeparator - 1));
        var signature = TryDecodeSegment(compactLicense.AsSpan(secondSeparator + 1));

        if (headerJson is null || payloadJson is null || signature is null)
        {
            detail = "The license segments must be base64url text.";

            return null;
        }

        if (signature.Length != SignatureLength)
        {
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"The signature must be {SignatureLength} bytes of fixed-field ECDSA P-256 output, not {signature.Length}.");

            return null;
        }

        var signingChain = TryReadSigningChain(headerJson, out detail);

        if (signingChain is null)
        {
            return null;
        }

        return new LicenseEnvelope(
            signingChain,
            payloadJson,
            Encoding.ASCII.GetBytes(compactLicense[..secondSeparator]),
            signature);
    }

    private static X509Certificate2[]? TryReadSigningChain(byte[] headerJson, out string detail)
    {
        detail = string.Empty;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(headerJson);
        }
        catch (JsonException)
        {
            detail = "The header segment is not valid JSON.";

            return null;
        }

        using (document)
        {
            var header = document.RootElement;

            if (header.ValueKind != JsonValueKind.Object)
            {
                detail = "The header must be a JSON object.";

                return null;
            }

            // The algorithm is pinned before anything else, so a document naming a weaker algorithm, or none
            // at all, does not change how it is checked.
            if (!header.TryGetProperty(LicenseJsonNames.Algorithm, out var algorithm)
                || algorithm.ValueKind != JsonValueKind.String
                || !string.Equals(algorithm.GetString(), LicenseDocument.Algorithm, StringComparison.Ordinal))
            {
                detail = $"The header must declare an '{LicenseJsonNames.Algorithm}' of '{LicenseDocument.Algorithm}'.";

                return null;
            }

            // The type is optional for compatibility with a document written before it was emitted, but a
            // document that states a different type is describing a format this reader does not implement.
            if (header.TryGetProperty(LicenseJsonNames.Type, out var type)
                && (type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), LicenseDocument.Type, StringComparison.Ordinal)))
            {
                detail = $"The header's '{LicenseJsonNames.Type}', when stated, must be '{LicenseDocument.Type}'.";

                return null;
            }

            if (!header.TryGetProperty(LicenseJsonNames.CertificateChain, out var chain)
                || chain.ValueKind != JsonValueKind.Array
                || chain.GetArrayLength() == 0)
            {
                detail = $"The header must carry a non-empty '{LicenseJsonNames.CertificateChain}' certificate array.";

                return null;
            }

            var certificates = new List<X509Certificate2>(chain.GetArrayLength());

            foreach (var entry in chain.EnumerateArray())
            {
                var certificate = entry.ValueKind == JsonValueKind.String
                    ? TryLoadCertificate(entry.GetString())
                    : null;

                if (certificate is null)
                {
                    Dispose(certificates);
                    detail = $"Every '{LicenseJsonNames.CertificateChain}' entry must be a base64 DER certificate.";

                    return null;
                }

                certificates.Add(certificate);
            }

            // The declared algorithm and the signing certificate have to describe the same key, otherwise the
            // document names ES256 while presenting a certificate no ES256 signature could have come from.
            // Settling it here also lets every later consumer take the signer's key as present and usable.
            if (!CarriesSigningKeyForEs256(certificates[0]))
            {
                Dispose(certificates);
                detail = $"The signing certificate in '{LicenseJsonNames.CertificateChain}' must carry an ECDSA P-256 public key.";

                return null;
            }

            return [.. certificates];
        }
    }

    private static bool CarriesSigningKeyForEs256(X509Certificate2 certificate)
    {
        ECDsa? key;

        try
        {
            key = certificate.GetECDsaPublicKey();
        }
        catch (CryptographicException)
        {
            // A certificate naming a curve the platform cannot construct, such as one with an invalid named
            // curve identifier, raises here instead of returning nothing. The document comes from outside, so
            // that outcome has to be a refusal like any other unusable certificate rather than an exception
            // escaping the reader.
            return false;
        }

        using (key)
        {
            return key is not null && LicenseClaimValidation.IsNistP256(key);
        }
    }

    private static void Dispose(List<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }

    private static X509Certificate2? TryLoadCertificate(string? base64Der)
    {
        if (string.IsNullOrEmpty(base64Der))
        {
            return null;
        }

        var buffer = new byte[base64Der.Length / 4 * 3 + 3];

        if (!Convert.TryFromBase64String(base64Der, buffer, out var written))
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(buffer.AsSpan(0, written));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static LicenseClaims? TryReadClaims(byte[] payloadJson, out LicenseFailureReason reason, out string detail)
    {
        reason = LicenseFailureReason.Malformed;
        detail = string.Empty;

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            detail = "The payload segment is not valid JSON.";

            return null;
        }

        using (document)
        {
            var payload = document.RootElement;

            if (payload.ValueKind != JsonValueKind.Object)
            {
                detail = "The payload must be a JSON object.";

                return null;
            }

            if (!payload.TryGetProperty(LicenseJsonNames.SchemaVersion, out var schemaVersionClaim)
                || schemaVersionClaim.ValueKind != JsonValueKind.Number
                || !schemaVersionClaim.TryGetInt32(out var schemaVersion)
                || schemaVersion < 1)
            {
                detail = $"The payload must carry a positive integer '{LicenseJsonNames.SchemaVersion}'.";

                return null;
            }

            // The version is settled before any other claim is read, so a payload written to a later schema
            // is reported as a version this build cannot read rather than as a shape this build rejects.
            if (schemaVersion > LicenseDocument.SchemaVersion)
            {
                reason = LicenseFailureReason.UnsupportedSchemaVersion;
                detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The license uses schema version {schemaVersion}; this build reads version {LicenseDocument.SchemaVersion}.");

                return null;
            }

            if (!TryReadRequiredString(
                    payload,
                    LicenseJsonNames.LicenseId,
                    LicenseClaimValidation.MaximumLicenseIdLength,
                    out var licenseId))
            {
                detail = $"The payload must carry a non-empty, bounded, control-free '{LicenseJsonNames.LicenseId}'.";

                return null;
            }

            if (!TryReadRequiredString(
                    payload,
                    LicenseJsonNames.Licensee,
                    LicenseClaimValidation.MaximumLicenseeLength,
                    out var licensee))
            {
                detail = $"The payload must carry a non-empty, bounded, control-free '{LicenseJsonNames.Licensee}'.";

                return null;
            }

            if (!TryReadUnixSeconds(payload, LicenseJsonNames.IssuedAt, out var issuedAt)
                || !TryReadUnixSeconds(payload, LicenseJsonNames.NotBefore, out var notBefore)
                || !TryReadUnixSeconds(payload, LicenseJsonNames.ExpiresAt, out var expiresAt))
            {
                detail = $"The payload must carry '{LicenseJsonNames.IssuedAt}', '{LicenseJsonNames.NotBefore}', and "
                         + $"'{LicenseJsonNames.ExpiresAt}' as unix seconds.";

                return null;
            }

            if (!LicenseClaimValidation.HasSupportedIssuedAt(issuedAt))
            {
                detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The '{LicenseJsonNames.IssuedAt}' claim must fall between {LicenseClaimValidation.EarliestIssuedAt:yyyy-MM-dd} and {LicenseClaimValidation.LatestIssuedAt:yyyy-MM-dd}.");

                return null;
            }

            // The issue instant is not ordered against the term, because a license signed after the term it
            // covers has begun states an earlier start than its own signature.
            if (!LicenseClaimValidation.HasSupportedTerm(notBefore, expiresAt))
            {
                detail = $"The payload must carry '{LicenseJsonNames.NotBefore}' < '{LicenseJsonNames.ExpiresAt}', and "
                         + "the expiry must leave room for the grace window.";

                return null;
            }

            if (!TryReadCapabilities(payload, out var capabilities))
            {
                detail = $"The payload must carry '{LicenseJsonNames.Capabilities}' as an array of non-empty, bounded "
                         + "names free of control characters.";

                return null;
            }

            if (!TryReadLimits(payload, out var limits))
            {
                detail = $"Every stated '{LicenseJsonNames.Limits}' member must be a non-negative whole number or '{LicenseJsonNames.Unlimited}'.";

                return null;
            }

            return new LicenseClaims
            {
                SchemaVersion = schemaVersion,
                LicenseId = licenseId,
                Licensee = licensee,
                IssuedAt = issuedAt,
                NotBefore = notBefore,
                ExpiresAt = expiresAt,
                Capabilities = capabilities,
                Limits = limits,
            };
        }
    }

    private static bool TryReadRequiredString(JsonElement payload, string name, int maximumLength, out string value)
    {
        value = string.Empty;

        if (!payload.TryGetProperty(name, out var claim) || claim.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = claim.GetString();

        if (text is null || !LicenseClaimValidation.IsBoundedControlFreeText(text, maximumLength))
        {
            return false;
        }

        value = text;

        return true;
    }

    private static bool TryReadUnixSeconds(JsonElement payload, string name, out DateTimeOffset value)
    {
        value = default;

        if (!payload.TryGetProperty(name, out var claim)
            || claim.ValueKind != JsonValueKind.Number
            || !claim.TryGetInt64(out var seconds)
            || seconds < MinimumUnixSeconds
            || seconds > MaximumUnixSeconds)
        {
            return false;
        }

        value = DateTimeOffset.FromUnixTimeSeconds(seconds);

        return true;
    }

    private static bool TryReadCapabilities(JsonElement payload, out IReadOnlyList<string> capabilities)
    {
        capabilities = [];

        if (!payload.TryGetProperty(LicenseJsonNames.Capabilities, out var claim) || claim.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var names = new List<string>(claim.GetArrayLength());

        foreach (var entry in claim.EnumerateArray())
        {
            // A capability name is carried into the product's capability set, so it is held to the same rule
            // as the other text claims instead of being taken from the document as written.
            if (entry.ValueKind != JsonValueKind.String
                || !LicenseClaimValidation.IsBoundedControlFreeText(entry.GetString(), LicenseClaimValidation.MaximumCapabilityLength))
            {
                return false;
            }

            names.Add(entry.GetString()!);
        }

        // The list the names were collected into is not handed out. A verified license is published through
        // its claims, and a caller that cast the collection back to its backing list could add a capability
        // the document never granted.
        capabilities = [.. names];

        return true;
    }

    private static bool TryReadLimits(JsonElement payload, out LicenseLimits limits)
    {
        limits = LicenseLimits.None;

        if (!payload.TryGetProperty(LicenseJsonNames.Limits, out var claim))
        {
            return true;
        }

        if (claim.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadLimit(claim, LicenseJsonNames.AuthorsPerMonth, out var authorsPerMonth)
            || !TryReadLimit(claim, LicenseJsonNames.Clients, out var clients)
            || !TryReadLimit(claim, LicenseJsonNames.Runners, out var runners)
            || !TryReadLimit(claim, LicenseJsonNames.ConcurrentReviews, out var concurrentReviews))
        {
            return false;
        }

        limits = new LicenseLimits
        {
            AuthorsPerMonth = authorsPerMonth,
            Clients = clients,
            Runners = runners,
            ConcurrentReviews = concurrentReviews,
        };

        return true;
    }

    private static bool TryReadLimit(JsonElement limits, string name, out LicenseLimit limit)
    {
        limit = LicenseLimit.Absent;

        if (!limits.TryGetProperty(name, out var member))
        {
            return true;
        }

        switch (member.ValueKind)
        {
            case JsonValueKind.Number when member.TryGetInt64(out var count) && count >= 0:
                limit = LicenseLimit.Of(count);

                return true;

            case JsonValueKind.String when string.Equals(member.GetString(), LicenseJsonNames.Unlimited, StringComparison.Ordinal):
                limit = LicenseLimit.Unlimited;

                return true;

            default:
                return false;
        }
    }

    private static byte[]? TryDecodeSegment(ReadOnlySpan<char> segment)
    {
        // The alphabet is checked here rather than left to the decoder, for two reasons. The decoder's
        // validity check and its decode call do not agree on what they accept: "AA=" passes the check and then
        // raises on decode, and a document arriving from outside has to produce a refusal rather than an
        // exception. The decoder also accepts padding and whitespace that this format does not, and accepting
        // them would give one license several byte forms that all read the same.
        foreach (var character in segment)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
            {
                return null;
            }
        }

        // The alphabet holds but the length may still not: a segment of 4n+1 characters encodes no byte count.
        if (!Base64Url.IsValid(segment, out var decodedLength))
        {
            return null;
        }

        var buffer = new byte[decodedLength];

        return Base64Url.TryDecodeFromChars(segment, buffer, out var written)
            ? buffer.AsSpan(0, written).ToArray()
            : null;
    }

    private sealed class LicenseEnvelope
    {
        public LicenseEnvelope(X509Certificate2[] signingChain, byte[] payloadJson, byte[] signedBytes, byte[] signature)
        {
            this.SigningChain = signingChain;
            this.PayloadJson = payloadJson;
            this.SignedBytes = signedBytes;
            this.Signature = signature;
        }

        public X509Certificate2[] SigningChain { get; }

        public byte[] PayloadJson { get; }

        public byte[] SignedBytes { get; }

        public byte[] Signature { get; }

        public void DisposeChain()
        {
            foreach (var certificate in this.SigningChain)
            {
                certificate.Dispose();
            }
        }
    }
}
