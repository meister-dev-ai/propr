// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Reads the committed golden license. It was signed once and frozen, so it detects a reader that stops
///     accepting documents this project already issued. A round trip through the writer would not: a format
///     change applied to both sides still passes it.
/// </summary>
public sealed class GoldenLicenseTests
{
    [Fact]
    public void TheGoldenLicense_ParsesAndVerifiesAgainstTheTestSigningCertificate()
    {
        using var publicKey = TestLicenseChain.LoadSignerPublicKey();

        var result = LicenseReader.Read(TestLicenseChain.ReadGoldenLicense(), publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        result.License.Dispose();
    }

    [Fact]
    public void TheGoldenLicense_CarriesEveryClaim()
    {
        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(TestLicenseChain.ReadGoldenLicense(), publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        var claims = license.Claims;
        Assert.Equal(1, claims.SchemaVersion);
        Assert.Equal("6c2a4d1e-8f13-4c0a-9b47-2d5e6f7a8b90", claims.LicenseId);
        Assert.Equal("ProPR Licensing Test Fixture", claims.Licensee);
        Assert.Equal(1767225600, claims.IssuedAt.ToUnixTimeSeconds());
        Assert.Equal(1767225600, claims.NotBefore.ToUnixTimeSeconds());
        Assert.Equal(2082758400, claims.ExpiresAt.ToUnixTimeSeconds());
        Assert.Equal(["review", "runners", "code-insights"], claims.Capabilities);
        Assert.Equal(LicenseLimit.Of(250), claims.Limits.AuthorsPerMonth);
        Assert.Equal(LicenseLimit.Unlimited, claims.Limits.Clients);
        Assert.Equal(LicenseLimit.Of(8), claims.Limits.Runners);
        Assert.Equal(LicenseLimit.Of(4), claims.Limits.ConcurrentReviews);
    }

    [Fact]
    public void TheGoldenLicense_CarriesTheTestSignerAndTheTestRoot()
    {
        var result = LicenseReader.Parse(TestLicenseChain.ReadGoldenLicense());

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        using var signer = TestLicenseChain.LoadSigningCertificate();
        using var root = TestLicenseChain.LoadRootCertificate();
        Assert.Equal(2, license.SigningChain.Count);
        Assert.Equal(signer.Thumbprint, license.SigningChain[0].Thumbprint);
        Assert.Equal(root.Thumbprint, license.SigningChain[1].Thumbprint);
    }

    // The frozen document has to stay the one the current writer would emit for the same claims. The
    // signature carries a fresh random nonce on every signing run, so only the two encoded segments it
    // covers can be compared.
    [Fact]
    public void TheGoldenLicense_HasTheHeaderAndPayloadTheWriterProducesFromTheSameClaims()
    {
        var written = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims()).Split('.');
        var golden = TestLicenseChain.ReadGoldenLicense().Split('.');

        Assert.Equal(golden[0], written[0]);
        Assert.Equal(golden[1], written[1]);
    }
}
