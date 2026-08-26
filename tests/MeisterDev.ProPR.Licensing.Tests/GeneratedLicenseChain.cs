// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Builds signing material at run time for the cases the committed fixture chain cannot express: a signing
///     certificate whose validity window is chosen by the test, and a certificate that presents itself as its
///     own root.
/// </summary>
internal static class GeneratedLicenseChain
{
    private static readonly DateTimeOffset RootNotBefore = new(2019, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RootNotAfter = new(2045, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] SigningCertificateSerialNumber = [0x4c, 0x69, 0x63, 0x02];
    private static readonly byte[] IntermediateSerialNumber = [0x4c, 0x69, 0x63, 0x04];

    // The named-curve object identifier of a P-256 public key, tag and length included, as it appears in the
    // subject public key info of a certificate.
    private static readonly byte[] NistP256CurveIdentifier = [0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07];

    /// <summary>
    ///     Issues a license from a fresh root and a signing certificate valid for the stated window. The window
    ///     has to fall inside the root's own, which spans well beyond every instant the tests use.
    /// </summary>
    /// <param name="claims">The claims to sign.</param>
    /// <param name="signerNotBefore">When the signing certificate becomes valid.</param>
    /// <param name="signerNotAfter">When the signing certificate stops being valid.</param>
    /// <returns>The document and the root to anchor a verifier at. The caller disposes the root.</returns>
    public static (string CompactLicense, X509Certificate2 Root) IssueLicense(
        LicenseClaims claims,
        DateTimeOffset signerNotBefore,
        DateTimeOffset signerNotAfter)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Generated License Root, O=Test Fixtures Only", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var issuingRoot = rootRequest.CreateSelfSigned(RootNotBefore, RootNotAfter);

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signerRequest = new CertificateRequest("CN=Generated License Signer, O=Test Fixtures Only", signingKey, HashAlgorithmName.SHA256);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        signerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(signerRequest.PublicKey, false));

        using var signingCertificate = signerRequest.Create(issuingRoot, signerNotBefore, signerNotAfter, SigningCertificateSerialNumber);

        var compactLicense = LicenseWriter.Sign(claims, signingKey, [signingCertificate, issuingRoot]);

        // The anchor an installation carries holds no private key, so the root handed back is the public part
        // on its own.
        return (compactLicense, X509CertificateLoader.LoadCertificate(issuingRoot.RawData));
    }

    /// <summary>
    ///     A self-signed root that issued nothing, which is what a test compares a built path's terminal
    ///     certificate against when it has to be one the path does not lead to.
    /// </summary>
    /// <returns>The root, without a private key. The caller disposes it.</returns>
    public static X509Certificate2 CreateUnrelatedRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Unrelated License Root, O=Test Fixtures Only", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(RootNotBefore, RootNotAfter);

        return X509CertificateLoader.LoadCertificate(certificate.RawData);
    }

    /// <summary>
    ///     Issues a license from a self-signed certificate, which the header then carries as the whole chain.
    ///     The document is internally consistent: the signature holds under the certificate it names, and the
    ///     certificate is its own issuer.
    /// </summary>
    /// <param name="claims">The claims to sign.</param>
    /// <returns>The document.</returns>
    public static string IssueSelfSignedLicense(LicenseClaims claims)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Self Issued License Signer, O=Test Fixtures Only", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(RootNotBefore, RootNotAfter);

        return LicenseWriter.Sign(claims, key, [certificate]);
    }

    /// <summary>
    ///     Issues a license from a certificate that carries a certificate authority's basic constraints and is
    ///     itself issued by the root. The path from it terminates at the root, so what the document fails on is
    ///     the signer being a certificate authority rather than the path.
    /// </summary>
    /// <param name="claims">The claims to sign.</param>
    /// <returns>The document and the root to anchor a verifier at. The caller disposes the root.</returns>
    public static (string CompactLicense, X509Certificate2 Root) IssueLicenseFromACertificateAuthority(LicenseClaims claims)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Generated License Root, O=Test Fixtures Only", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var issuingRoot = rootRequest.CreateSelfSigned(RootNotBefore, RootNotAfter);

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest(
            "CN=Generated License Intermediate, O=Test Fixtures Only",
            intermediateKey,
            HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));

        using var intermediate = intermediateRequest.Create(issuingRoot, RootNotBefore, RootNotAfter, IntermediateSerialNumber);

        var compactLicense = LicenseWriter.Sign(claims, intermediateKey, [intermediate, issuingRoot]);

        return (compactLicense, X509CertificateLoader.LoadCertificate(issuingRoot.RawData));
    }

    /// <summary>
    ///     A certificate whose public key names a curve identifier nothing implements, made by altering the
    ///     named-curve identifier of a P-256 certificate. The certificate still parses, so it reaches the point
    ///     where its key is read, and reading the key is what fails.
    /// </summary>
    /// <returns>The certificate. The caller disposes it.</returns>
    public static X509Certificate2 CreateCertificateNamingAnUnknownCurve()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Unknown Curve Signer, O=Test Fixtures Only", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(RootNotBefore, RootNotAfter);

        var der = certificate.RawData;
        var offset = der.AsSpan().IndexOf(NistP256CurveIdentifier);
        Assert.True(offset >= 0, "The certificate does not carry the expected named-curve identifier.");

        // The last arc of the identifier is the curve number. Raising it leaves the encoding the same length
        // and names a curve that is not assigned, so the certificate parses and its key does not.
        der[offset + NistP256CurveIdentifier.Length - 1] = 0x63;

        return X509CertificateLoader.LoadCertificate(der);
    }

    /// <summary>Issues a self-signed document from raw payload JSON for reader refusal tests.</summary>
    /// <param name="payloadJson">The payload to sign.</param>
    /// <returns>The document.</returns>
    public static string IssueSelfSignedRawLicense(string payloadJson)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Self Issued License Signer, O=Test Fixtures Only", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(RootNotBefore, RootNotAfter);

        return RawLicense.SignWith(key, RawLicense.HeaderJson([certificate]), payloadJson);
    }
}
