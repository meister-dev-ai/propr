// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Access to the committed test signing chain and the golden license under <c>Fixtures</c>.
///     <para>
///         The key and certificates there are test material. They sign nothing outside this project and
///         confer nothing, so they are checked in like any other fixture.
///     </para>
/// </summary>
internal static class TestLicenseChain
{
    public const string RootCertificateFile = "test-only-license-root.pem";
    public const string SigningCertificateFile = "test-only-license-signer.pem";
    public const string SigningKeyFile = "test-only-license-signer-key.pem";
    public const string GoldenLicenseFile = "golden-license.txt";

    public static X509Certificate2 LoadRootCertificate() => X509Certificate2.CreateFromPem(ReadFixture(RootCertificateFile));

    public static X509Certificate2 LoadSigningCertificate() => X509Certificate2.CreateFromPem(ReadFixture(SigningCertificateFile));

    public static ECDsa LoadSigningKey()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(ReadFixture(SigningKeyFile));

        return key;
    }

    /// <summary>
    ///     The public key of the fixture signing certificate. It is taken from a parsed document rather than
    ///     from the certificate directly, because reading a document establishes that the signing certificate
    ///     carries a usable ECDSA P-256 key and the accessor therefore needs no null handling here.
    /// </summary>
    public static ECDsa LoadSignerPublicKey()
    {
        using var parsed = ParseGoldenLicense();

        return parsed.CreateSignerPublicKey();
    }

    /// <summary>The golden license, parsed. Its signing certificate is the one the fixtures sign with.</summary>
    public static ParsedLicense ParseGoldenLicense()
    {
        var result = LicenseReader.Parse(ReadGoldenLicense());
        Assert.True(result.IsSuccess, result.FailureDetail);

        return result.License;
    }

    /// <summary>The header chain the fixtures sign with: the signer, then the root that issued it.</summary>
    public static IReadOnlyList<X509Certificate2> LoadSigningChain() => [LoadSigningCertificate(), LoadRootCertificate()];

    public static string ReadGoldenLicense() => ReadFixture(GoldenLicenseFile).Trim();

    /// <summary>Signs claims with the test chain, so a test only states what it wants signed.</summary>
    public static string Sign(LicenseClaims claims)
    {
        using var signingKey = LoadSigningKey();
        var chain = LoadSigningChain();

        try
        {
            return LicenseWriter.Sign(claims, signingKey, chain);
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    ///     The claims the golden license carries. Shared with the fixture generator so a regenerated fixture
    ///     and the tests that read it never disagree about what a fully populated license looks like.
    /// </summary>
    public static LicenseClaims GoldenClaims() => new()
    {
        LicenseId = "6c2a4d1e-8f13-4c0a-9b47-2d5e6f7a8b90",
        Licensee = "ProPR Licensing Test Fixture",
        IssuedAt = DateTimeOffset.FromUnixTimeSeconds(1767225600),
        NotBefore = DateTimeOffset.FromUnixTimeSeconds(1767225600),
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(2082758400),
        Capabilities = ["review", "runners", "code-insights"],
        Limits = new LicenseLimits
        {
            AuthorsPerMonth = LicenseLimit.Of(250),
            Clients = LicenseLimit.Unlimited,
            Runners = LicenseLimit.Of(8),
            ConcurrentReviews = LicenseLimit.Of(4),
        },
    };

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
