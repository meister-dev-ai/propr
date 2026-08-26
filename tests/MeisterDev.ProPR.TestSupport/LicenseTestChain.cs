// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     A signing chain generated for one test, and the anchor that accepts it.
///     <para>
///         The chain is built at run time rather than read from a committed fixture, so a test states the term
///         it wants a license to carry without a second file having to agree with it. Signing goes through the
///         same writer the issuing tool uses, so the documents these tests store are documents the product's
///         own verifier accepts.
///     </para>
/// </summary>
public sealed class LicenseTestChain : IDisposable
{
    private static readonly DateTimeOffset ChainNotBefore = new(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ChainNotAfter = new(2045, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] SigningCertificateSerialNumber = [0x4c, 0x69, 0x63, 0x03];

    private readonly X509Certificate2 _root;
    private readonly ECDsa _rootKey;

    private LicenseTestChain(ECDsa rootKey, X509Certificate2 root)
    {
        this._rootKey = rootKey;
        this._root = root;
    }

    /// <summary>Generates a fresh root.</summary>
    /// <returns>The chain. The caller disposes it.</returns>
    public static LicenseTestChain Create()
    {
        var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Infrastructure License Root, O=Test Fixtures Only", rootKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return new LicenseTestChain(rootKey, request.CreateSelfSigned(ChainNotBefore, ChainNotAfter));
    }

    /// <summary>Claims for a license whose term runs over the supplied window.</summary>
    /// <param name="notBefore">The first moment the license is in force.</param>
    /// <param name="expiresAt">The moment it stops being in force.</param>
    /// <returns>The claims.</returns>
    public static LicenseClaims ClaimsFor(DateTimeOffset notBefore, DateTimeOffset expiresAt) => new()
    {
        LicenseId = "0f4c1b7a-6d21-4f36-9e18-5a7b3c9d2e40",
        Licensee = "ProPR Licensing Test Fixtures",
        IssuedAt = notBefore,
        NotBefore = notBefore,
        ExpiresAt = expiresAt,
        Capabilities = ["review", "runners"],
        Limits = new LicenseLimits { Runners = LicenseLimit.Of(4) },
    };

    /// <inheritdoc />
    public void Dispose()
    {
        this._root.Dispose();
        this._rootKey.Dispose();
    }

    /// <summary>
    ///     An anchor over this chain's root. Each call hands out its own copy, because an anchor takes
    ///     ownership of the certificate it holds.
    /// </summary>
    /// <returns>The anchor. The caller disposes it.</returns>
    public LicenseTrustAnchor CreateAnchor()
        => LicenseTrustAnchor.Of(X509CertificateLoader.LoadCertificate(this._root.RawData));

    /// <summary>Signs claims with a certificate this chain's root issued.</summary>
    /// <param name="claims">The claims to sign.</param>
    /// <returns>The compact license document.</returns>
    public string Sign(LicenseClaims claims)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Infrastructure License Signer, O=Test Fixtures Only", signingKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var signingCertificate = request.Create(this._root, ChainNotBefore, ChainNotAfter, SigningCertificateSerialNumber);

        return LicenseWriter.Sign(claims, signingKey, [signingCertificate, this._root]);
    }

    /// <summary>
    ///     Alters one character of a document's payload segment.
    ///     <para>
    ///         The result is still three base64url segments carrying the same certificates, so it reaches the
    ///         signature check rather than being turned away as malformed. That check is what a tampered
    ///         payload has to fail.
    ///     </para>
    /// </summary>
    /// <param name="compactLicense">The document to alter.</param>
    /// <returns>The altered document.</returns>
    public static string TamperPayload(string compactLicense)
    {
        var firstSeparator = compactLicense.IndexOf('.', StringComparison.Ordinal);
        var secondSeparator = compactLicense.IndexOf('.', firstSeparator + 1);
        var target = (firstSeparator + secondSeparator) / 2;

        return string.Concat(
            compactLicense.AsSpan(0, target),
            compactLicense[target] == 'A' ? "B" : "A",
            compactLicense.AsSpan(target + 1));
    }
}
