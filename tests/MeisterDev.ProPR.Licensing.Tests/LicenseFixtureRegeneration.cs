// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Rebuilds the committed test chain and the golden license.
///     <para>
///         The golden license is checked in so the suite reads a document this build did not just produce. A
///         format change applied to the writer and the reader together would pass a round-trip test and fail
///         against the frozen document. Regenerating the fixture therefore removes that check for the change
///         that prompted it, so this test stays skipped until the format itself changes.
///     </para>
/// </summary>
public sealed class LicenseFixtureRegeneration
{
    private const string Notice =
        "# TEST FIXTURE ONLY. This material signs license documents for the licensing test suite.\n"
        + "# It is not a product signing identity, it authorizes nothing, and it belongs to no installation.\n";

    private static readonly DateTimeOffset RootNotBefore = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RootNotAfter = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignerNotBefore = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignerNotAfter = new(2039, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact(Skip = "Rewrites the committed test chain and golden license. Remove the skip and run it once when the document format changes.")]
    public void RegenerateTheTestChainAndTheGoldenLicense()
    {
        var fixtures = SourceFixtureDirectory();
        Directory.CreateDirectory(fixtures);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=ProPR License Test Root, O=Test Fixtures Only", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var rootCertificate = rootRequest.CreateSelfSigned(RootNotBefore, RootNotAfter);

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signerRequest = new CertificateRequest("CN=ProPR License Test Signer, O=Test Fixtures Only", signingKey, HashAlgorithmName.SHA256);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        signerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(signerRequest.PublicKey, false));
        using var signingCertificate = signerRequest.Create(rootCertificate, SignerNotBefore, SignerNotAfter, [0x4c, 0x69, 0x63, 0x01]);

        Write(fixtures, TestLicenseChain.RootCertificateFile, Notice + rootCertificate.ExportCertificatePem());
        Write(fixtures, TestLicenseChain.SigningCertificateFile, Notice + signingCertificate.ExportCertificatePem());
        Write(fixtures, TestLicenseChain.SigningKeyFile, Notice + signingKey.ExportPkcs8PrivateKeyPem());

        var golden = LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [signingCertificate, rootCertificate]);
        Write(fixtures, TestLicenseChain.GoldenLicenseFile, golden);

        using var publicKey = signingCertificate.GetECDsaPublicKey()!;
        Assert.True(LicenseReader.Read(golden, publicKey).IsSuccess);
    }

    private static void Write(string directory, string name, string content)
        => File.WriteAllText(Path.Combine(directory, name), content.EndsWith('\n') ? content : content + "\n");

    // The fixtures are read from the build output at test time but have to be written back to the source
    // tree to be committed, so the generator locates the directory next to its own source file.
    private static string SourceFixtureDirectory([CallerFilePath] string callerFilePath = "")
        => Path.Combine(Path.GetDirectoryName(callerFilePath)!, "Fixtures");
}
