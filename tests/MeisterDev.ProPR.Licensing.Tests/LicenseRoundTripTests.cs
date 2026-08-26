// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MeisterDev.ProPR.Licensing.Tests;

public sealed class LicenseRoundTripTests
{
    [Fact]
    public void EveryClaim_SurvivesSigningAndReading()
    {
        var claims = TestLicenseChain.GoldenClaims();
        var compact = TestLicenseChain.Sign(claims);

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.Equal(claims.SchemaVersion, license.Claims.SchemaVersion);
        Assert.Equal(claims.LicenseId, license.Claims.LicenseId);
        Assert.Equal(claims.Licensee, license.Claims.Licensee);
        Assert.Equal(claims.IssuedAt, license.Claims.IssuedAt);
        Assert.Equal(claims.NotBefore, license.Claims.NotBefore);
        Assert.Equal(claims.ExpiresAt, license.Claims.ExpiresAt);
        Assert.Equal(claims.Capabilities, license.Claims.Capabilities);
        Assert.Equal(claims.Limits, license.Claims.Limits);
    }

    // A term is agreed before the document that carries it is signed, so a license issued on the fifth for a
    // term that began on the first has to sign and read back.
    [Fact]
    public void ATermThatBeganBeforeTheDocumentWasSigned_IsSignedAndReadBack()
    {
        var claims = TestLicenseChain.GoldenClaims();
        var backDated = claims with { IssuedAt = claims.NotBefore.AddDays(4) };

        var compact = TestLicenseChain.Sign(backDated);

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.Equal(backDated.IssuedAt, license.Claims.IssuedAt);
        Assert.Equal(backDated.NotBefore, license.Claims.NotBefore);
        Assert.True(license.Claims.NotBefore < license.Claims.IssuedAt);
    }

    // The reader collects the capability names into a list. Publishing that list on the claims would let
    // in-process code cast it back and add a capability to a license that was verified without it.
    [Fact]
    public void TheCapabilitiesOfAReadLicense_AreNotTheListTheReaderCollectedThemInto()
    {
        using var license = TestLicenseChain.ParseGoldenLicense();

        Assert.False(license.Claims.Capabilities is ICollection<string> { IsReadOnly: false });
        Assert.Equal(["review", "runners", "code-insights"], license.Claims.Capabilities);
    }

    [Fact]
    public void TheCompactForm_IsThreeUnpaddedBase64UrlSegments()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());

        Assert.Equal(3, compact.Split('.').Length);
        Assert.DoesNotContain('=', compact);
        Assert.DoesNotContain('+', compact);
        Assert.DoesNotContain('/', compact);
    }

    // A consumer picks the verification key out of the header, so the issuer's order has to survive reading.
    [Fact]
    public void TheHeaderChain_IsCarriedThroughSignerFirst()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());

        var result = LicenseReader.Parse(compact);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        using var signer = TestLicenseChain.LoadSigningCertificate();
        using var root = TestLicenseChain.LoadRootCertificate();
        Assert.Equal(2, license.SigningChain.Count);
        Assert.Equal(signer.Thumbprint, license.SigningCertificate.Thumbprint);
        Assert.Equal(root.Thumbprint, license.SigningChain[1].Thumbprint);
    }

    // A license issued by a newer tool has to keep working here, so a claim this build does not know is
    // dropped instead of refused.
    [Fact]
    public void AnUnknownClaim_IsIgnored()
    {
        var compact = RawLicense.SignWithClaim("issuedByRegion", "\"eu-central\"");

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.Equal("Raw Document Fixture", license.Claims.Licensee);
    }

    [Fact]
    public void AnUnknownHeaderParameter_IsIgnored()
    {
        var compact = RawLicense.Sign(
            RawLicense.HeaderJson(extraParameters: ",\"kid\":\"issuing-station-2\""),
            RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        result.License.Dispose();
    }

    [Fact]
    public void ATamperedPayload_FailsSignatureVerification()
    {
        var claims = RawLicense.AcceptedClaims();
        var compact = RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims));
        claims["licensee"] = "\"Somebody Else\"";
        var tampered = RawLicense.ReplacePayload(compact, RawLicense.PayloadJson(claims));

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(tampered, publicKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, result.FailureReason);

        var parsed = LicenseReader.Parse(tampered);
        Assert.True(parsed.IsSuccess, parsed.FailureDetail);
        using var license = parsed.License;
        Assert.False(license.HasValidSignature(publicKey));
    }

    // Reading with a different key has to fail even though the document itself is intact. Without that, the
    // signature would show only that the document was signed, not by whom.
    [Fact]
    public void AnotherKey_DoesNotVerifyTheDocument()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var result = LicenseReader.Read(compact, otherKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, result.FailureReason);
    }

    // The signature has to be checked before a claim is read, otherwise a caller could act on a claim that
    // nothing vouches for. The selector runs first, so a rejected chain reports the signer rather than the
    // payload.
    [Fact]
    public void TheSelectorChoosesTheKeyFromTheHeaderChain()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());
        IReadOnlyList<X509Certificate2>? offered = null;

        var result = LicenseReader.Read(
            compact, chain =>
            {
                offered = chain;

                return chain[0].GetECDsaPublicKey();
            });

        Assert.True(result.IsSuccess, result.FailureDetail);
        using var license = result.License;
        Assert.NotNull(offered);
        Assert.Equal(2, offered.Count);
        Assert.Equal("ProPR Licensing Test Fixture", license.Claims.Licensee);
    }

    [Fact]
    public void ASelectorThatAcceptsNoSigner_ReportsAnUntrustedSigner()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());

        var result = LicenseReader.Read(compact, _ => null);

        Assert.False(result.IsSuccess);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, result.FailureReason);
    }

    [Fact]
    public void ASelectorThatThrows_ReleasesTheHeaderCertificates()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());
        X509Certificate2? certificate = null;

        Assert.Throws<InvalidOperationException>(() => LicenseReader.Read(
            compact,
            chain =>
            {
                certificate = chain[0];
                throw new InvalidOperationException("The selector failed.");
            }));

        Assert.NotNull(certificate);
        Assert.Throws<CryptographicException>(() => certificate.ExportCertificatePem());
    }

    // A schema version this build cannot read is reported only once the document is known to come from the
    // signer, so an unverified document never gets to steer the diagnostic.
    [Fact]
    public void ASelectorThatAcceptsNoSigner_OutranksAnUnsupportedSchemaVersion()
    {
        var claims = RawLicense.AcceptedClaims();
        claims["schemaVersion"] = (LicenseDocument.SchemaVersion + 1).ToString();
        var compact = RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims));

        var result = LicenseReader.Read(compact, _ => null);

        Assert.Equal(LicenseFailureReason.UntrustedSigner, result.FailureReason);
    }

    // The format carries the signature as the fixed-width r and s values concatenated, not as the DER sequence
    // the .NET default produces, so the two encodings are pinned apart without reading the fixture.
    [Fact]
    public void TheSignature_IsJoseFixedFieldRatherThanDerEncoded()
    {
        var segments = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims()).Split('.');
        var signedBytes = Encoding.ASCII.GetBytes(string.Join('.', segments[0], segments[1]));
        var signature = Base64Url.DecodeFromChars(segments[2]);

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        Assert.Equal(64, signature.Length);
        Assert.True(
            publicKey.VerifyData(
                signedBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(
            publicKey.VerifyData(
                signedBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
    }
}
