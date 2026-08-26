// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeisterDev.ProPR.Licensing.Tests;

/// <summary>
///     Covers what the verifier accepts, what it refuses, and which of two problems it reports first.
/// </summary>
public sealed class LicenseVerificationTests
{
    // The golden license runs from 2026-01-01 to 2036-01-01, so this instant sits inside its term and the
    // outcome of a test that uses it turns on the signer rather than on the term.
    private static readonly DateTimeOffset InsideTheGoldenTerm = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AVerifiedLicenseCannotBeConstructedOutsideVerification()
    {
        Assert.Empty(typeof(VerifiedLicense).GetConstructors());
    }

    [Fact]
    public void ALicenseSignedByTheTestChain_VerifiesWithItsTermRunning()
    {
        using var anchor = TestChainAnchor();
        var verifier = new LicenseVerifier(anchor);

        var result = verifier.Verify(TestLicenseChain.ReadGoldenLicense(), InsideTheGoldenTerm);

        Assert.True(result.IsVerified, result.FailureDetail);
        Assert.Equal(LicenseTermStatus.Active, result.License.TermStatus);
        Assert.Equal("ProPR Licensing Test Fixture", result.License.Claims.Licensee);
        Assert.Equal(["review", "runners", "code-insights"], result.License.Claims.Capabilities);
        Assert.Equal(LicenseLimit.Of(250), result.License.Claims.Limits.AuthorsPerMonth);
    }

    // The issuer decides how much of the chain to carry. A header holding the signing certificate alone still
    // reaches the anchor, because the anchor is available to build the path from as well as to trust.
    [Fact]
    public void ALicenseCarryingOnlyItsSigningCertificate_Verifies()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signingCertificate = TestLicenseChain.LoadSigningCertificate();
        var compactLicense = LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [signingCertificate]);

        using var anchor = TestChainAnchor();
        var result = new LicenseVerifier(anchor).Verify(compactLicense, InsideTheGoldenTerm);

        Assert.True(result.IsVerified, result.FailureDetail);
    }

    // The certificates in the header say who signed. A signature made by another key does not match them, so
    // the document did not come from the signer it names.
    [Fact]
    public void ALicenseSignedByAnUnrelatedKey_ReportsAnUntrustedSigner()
    {
        using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var compactLicense = RawLicense.SignWith(
            unrelatedKey,
            RawLicense.HeaderJson(),
            RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        AssertRefused(compactLicense, LicenseFailureReason.UntrustedSigner);
    }

    [Fact]
    public void ALicenseAlteredAfterSigning_ReportsAnUntrustedSigner()
    {
        var claims = RawLicense.AcceptedClaims();
        var compactLicense = RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims));
        claims["licensee"] = "\"Somebody Else\"";

        AssertRefused(RawLicense.ReplacePayload(compactLicense, RawLicense.PayloadJson(claims)), LicenseFailureReason.UntrustedSigner);
    }

    // The document is consistent with itself: the signature holds under the certificate it names and that
    // certificate issued itself. What it cannot do is lead to the anchor, which is what acceptance rests on.
    [Fact]
    public void ASelfSignedCertificatePresentedAsItsOwnRoot_ReportsAnUntrustedSigner()
    {
        AssertRefused(GeneratedLicenseChain.IssueSelfSignedLicense(TestLicenseChain.GoldenClaims()), LicenseFailureReason.UntrustedSigner);
    }

    // A complete and internally sound chain from another issuer is what an anchor exists to rule out: every
    // certificate in it checks out against the one above, and none of them is the anchor.
    [Fact]
    public void ALicenseFromAnotherIssuersChain_ReportsAnUntrustedSigner()
    {
        var (compactLicense, foreignRoot) = GeneratedLicenseChain.IssueLicense(
            TestLicenseChain.GoldenClaims(),
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // The document is offered to a verifier anchored at the test root, so the root that issued it is
        // released rather than used.
        foreignRoot.Dispose();

        AssertRefused(compactLicense, LicenseFailureReason.UntrustedSigner);
    }

    // The certificate authority issues a signing certificate whose basic constraints state it is not one. A
    // certificate authority under the anchor can build a path to it, so without pinning the signer to an
    // end-entity certificate an intermediate could sign licenses on its own.
    [Fact]
    public void ALicenseSignedByACertificateAuthorityUnderTheAnchor_ReportsAnUntrustedSigner()
    {
        var (compactLicense, root) = GeneratedLicenseChain.IssueLicenseFromACertificateAuthority(TestLicenseChain.GoldenClaims());

        using var anchor = LicenseTrustAnchor.Of(root);
        var result = new LicenseVerifier(anchor).Verify(compactLicense, InsideTheGoldenTerm);

        Assert.False(result.IsVerified);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, result.FailureReason);
        Assert.Contains("end-entity", result.FailureDetail, StringComparison.Ordinal);
    }

    // Creating the chain reads the anchor and loads a copy of it, so an anchor that can no longer be read
    // raises before the path is built. That is an input this build cannot process, which is a refusal like any
    // other on this path rather than a fault reaching the caller.
    [Fact]
    public void AnAnchorThatCanNoLongerBeRead_IsRefusedRatherThanRaised()
    {
        using var parsed = TestLicenseChain.ParseGoldenLicense();
        var releasedAnchor = TestLicenseChain.LoadRootCertificate();
        releasedAnchor.Dispose();

        Assert.False(LicenseChainPolicy.LeadsToAnchor(parsed.SigningChain, releasedAnchor, InsideTheGoldenTerm, out var failureDetail));
        Assert.False(string.IsNullOrWhiteSpace(failureDetail));
    }

    // The signature is settled before the payload is read, so a document that fails it reports the signer
    // whatever else its payload states.
    [Fact]
    public void ABrokenSignature_IsReportedAheadOfAnUnsupportedSchemaVersion()
    {
        var claims = RawLicense.AcceptedClaims();
        claims["schemaVersion"] = (LicenseDocument.SchemaVersion + 1).ToString();

        using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var compactLicense = RawLicense.SignWith(unrelatedKey, RawLicense.HeaderJson(), RawLicense.PayloadJson(claims));

        AssertRefused(compactLicense, LicenseFailureReason.UntrustedSigner);
    }

    // The anchor is looked at after the signature as well, so a build carrying none still reports the signer
    // for a document whose signature does not hold.
    [Fact]
    public void ABrokenSignature_IsReportedAheadOfAMissingAnchor()
    {
        using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var compactLicense = RawLicense.SignWith(
            unrelatedKey,
            RawLicense.HeaderJson(),
            RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        using var anchor = LicenseTrustAnchor.None();
        var result = new LicenseVerifier(anchor).Verify(compactLicense, InsideTheGoldenTerm);

        Assert.False(result.IsVerified);
        Assert.Equal(LicenseFailureReason.UntrustedSigner, result.FailureReason);
    }

    [Theory]
    [InlineData("RS256")]
    [InlineData("none")]
    public void ADocumentDeclaringAnotherAlgorithm_IsMalformed(string algorithm)
    {
        var compactLicense = RawLicense.Sign(RawLicense.HeaderJson(algorithm), RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        AssertRefused(compactLicense, LicenseFailureReason.Malformed);
    }

    [Fact]
    public void ALicenseDeclaringALaterSchemaVersion_ReportsAnUnsupportedSchemaVersion()
    {
        var claims = RawLicense.AcceptedClaims();
        claims["schemaVersion"] = (LicenseDocument.SchemaVersion + 1).ToString();
        var compactLicense = RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims));

        AssertRefused(compactLicense, LicenseFailureReason.UnsupportedSchemaVersion);
    }

    // A document is reported as one this build cannot read before it is reported as coming from a signer this
    // build does not accept. The two are established in that order: the schema version is settled while reading
    // the document, and the certificate path is built after that.
    [Fact]
    public void ALaterSchemaVersion_IsReportedAheadOfAnUntrustedSigner()
    {
        var claims = RawLicense.AcceptedClaims();
        claims["schemaVersion"] = (LicenseDocument.SchemaVersion + 1).ToString();
        var compactLicense = GeneratedLicenseChain.IssueSelfSignedRawLicense(RawLicense.PayloadJson(claims));

        AssertRefused(compactLicense, LicenseFailureReason.UnsupportedSchemaVersion);
    }

    // The issue instant reaches a certificate chain engine as the moment validity is evaluated at, and a
    // document can state one no engine can represent. It is bounded when the document is read, so the caller
    // gets a refusal rather than an exception raised from inside a platform library.
    [Theory]
    [InlineData("-62135596800", "1970-01-01")]
    [InlineData("253402300800", "unix seconds")]
    public void AnIssueInstantOutsideTheAcceptedRange_IsMalformed(string issuedAtJson, string expectedDetail)
    {
        using var anchor = TestChainAnchor();

        var result = new LicenseVerifier(anchor).Verify(RawLicense.SignWithClaim("iat", issuedAtJson), InsideTheGoldenTerm);

        Assert.False(result.IsVerified);
        Assert.Equal(LicenseFailureReason.Malformed, result.FailureReason);
        Assert.Contains(expectedDetail, result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerifierWithoutAnAnchor_ReportsThatThisBuildCarriesNone()
    {
        using var anchor = LicenseTrustAnchor.None();

        var result = new LicenseVerifier(anchor).Verify(TestLicenseChain.ReadGoldenLicense(), InsideTheGoldenTerm);

        Assert.False(result.IsVerified);
        Assert.Equal(LicenseFailureReason.NoAnchorInThisBuild, result.FailureReason);
    }

    // A build without an anchor accepts nothing, but the operator still gets the reason that applies to the
    // file in front of them rather than one that applies to every file.
    [Fact]
    public void AMalformedDocument_IsMalformedEvenWithoutAnAnchor()
    {
        using var anchor = LicenseTrustAnchor.None();

        var result = new LicenseVerifier(anchor).Verify("not-a-license", InsideTheGoldenTerm);

        Assert.False(result.IsVerified);
        Assert.Equal(LicenseFailureReason.Malformed, result.FailureReason);
    }

    // Certificate validity is evaluated at the instant the license was issued. A signing certificate lives for
    // a shorter period than the licenses it signs, so a license issued while the certificate was valid keeps
    // running for its full term once that certificate has expired.
    [Fact]
    public void ALicenseWhoseSigningCertificateHasSinceExpired_StillVerifies()
    {
        var issuedAt = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var claims = TestLicenseChain.GoldenClaims() with
        {
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var (compactLicense, root) = GeneratedLicenseChain.IssueLicense(
            claims,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        using var anchor = LicenseTrustAnchor.Of(root);
        var result = new LicenseVerifier(anchor).Verify(compactLicense, now);

        Assert.True(result.IsVerified, result.FailureDetail);
        Assert.Equal(LicenseTermStatus.Active, result.License.TermStatus);

        // The same certificates evaluated at the current instant do not build a path, which is what makes the
        // outcome above a consequence of the issue instant rather than of a certificate that is valid anyway.
        using var parsed = LicenseReader.Parse(compactLicense).License!;
        Assert.False(LicenseChainPolicy.LeadsToAnchor(parsed.SigningChain, anchor.Certificate!, now, out _));
    }

    // The instants are the golden license's not-before and expiry claims, and one second either side of each.
    // A license outside its term still verifies: the term is reported on the verified license rather than
    // standing in the way of establishing who signed it.
    [Theory]
    [InlineData(1767225599, LicenseTermStatus.NotYetValid)]
    [InlineData(1767225600, LicenseTermStatus.Active)]
    [InlineData(2082758399, LicenseTermStatus.Active)]
    [InlineData(2082758400, LicenseTermStatus.Expired)]
    [InlineData(2082758401, LicenseTermStatus.Expired)]
    public void TheTermStatus_FollowsTheInstantTheLicenseIsCheckedAt(long nowUnixSeconds, LicenseTermStatus expected)
    {
        using var anchor = TestChainAnchor();

        var result = new LicenseVerifier(anchor).Verify(
            TestLicenseChain.ReadGoldenLicense(),
            DateTimeOffset.FromUnixTimeSeconds(nowUnixSeconds));

        Assert.True(result.IsVerified, result.FailureDetail);
        Assert.Equal(expected, result.License.TermStatus);
        Assert.Equal(1767225600, result.License.Claims.NotBefore.ToUnixTimeSeconds());
        Assert.Equal(2082758400, result.License.Claims.ExpiresAt.ToUnixTimeSeconds());
    }

    // One anchor serves an installation for as long as it runs, so verifying must leave the certificate it
    // holds usable.
    [Fact]
    public void TheAnchor_ServesMoreThanOneVerification()
    {
        using var anchor = TestChainAnchor();
        var verifier = new LicenseVerifier(anchor);

        Assert.True(verifier.Verify(TestLicenseChain.ReadGoldenLicense(), InsideTheGoldenTerm).IsVerified);
        Assert.True(verifier.Verify(TestLicenseChain.Sign(TestLicenseChain.GoldenClaims()), InsideTheGoldenTerm).IsVerified);
        Assert.True(anchor.IsPresent);
    }

    [Fact]
    public void TheChainPolicy_TrustsTheAnchorAloneAndCarriesTheHeaderCertificatesAsPathMaterial()
    {
        using var root = TestLicenseChain.LoadRootCertificate();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        var chain = LicenseChainPolicy.CreateChain([signer, root], root, InsideTheGoldenTerm);

        try
        {
            Assert.Equal(X509ChainTrustMode.CustomRootTrust, chain.ChainPolicy.TrustMode);

            // The anchor is the only certificate a path may terminate at, and the certificates the header
            // carries beyond the signer are material to build that path from. The trust store holds a copy of
            // the anchor rather than the instance handed in, so the certificates are compared by thumbprint.
            var trusted = Assert.IsType<X509Certificate2>(Assert.Single(chain.ChainPolicy.CustomTrustStore));
            Assert.Equal(root.Thumbprint, trusted.Thumbprint);
            Assert.NotSame(root, trusted);
            var pathMaterial = Assert.IsType<X509Certificate2>(Assert.Single(chain.ChainPolicy.ExtraStore));
            Assert.Equal(root.Thumbprint, pathMaterial.Thumbprint);
        }
        finally
        {
            LicenseChainPolicy.DisposeChain(chain);
        }
    }

    [Fact]
    public void TheChainPolicy_ChecksNoRevocationDownloadsNothingAndJudgesValidityAtTheGivenInstant()
    {
        using var root = TestLicenseChain.LoadRootCertificate();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        var chain = LicenseChainPolicy.CreateChain([signer, root], root, InsideTheGoldenTerm);

        try
        {
            Assert.Equal(X509RevocationMode.NoCheck, chain.ChainPolicy.RevocationMode);
            Assert.True(chain.ChainPolicy.DisableCertificateDownloads);
            Assert.False(chain.ChainPolicy.VerificationTimeIgnored);
            Assert.Equal(InsideTheGoldenTerm.UtcDateTime, chain.ChainPolicy.VerificationTime);
            Assert.Equal(DateTimeKind.Utc, chain.ChainPolicy.VerificationTime.Kind);
        }
        finally
        {
            LicenseChainPolicy.DisposeChain(chain);
        }
    }

    // The guard repeats a condition a successful build already meets, so a document alone cannot tell whether
    // it holds. It is exercised here against the chain the fixtures build and against a root that chain never
    // reaches.
    [Fact]
    public void TheTerminalCertificateGuard_AcceptsThePathsAnchorAndRefusesAnother()
    {
        using var root = TestLicenseChain.LoadRootCertificate();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        using var unrelatedRoot = GeneratedLicenseChain.CreateUnrelatedRoot();

        var chain = LicenseChainPolicy.CreateChain([signer, root], root, InsideTheGoldenTerm);

        try
        {
            Assert.True(chain.Build(signer));
            Assert.True(LicenseChainPolicy.TerminatesAt(chain, root));
            using var rootCopy = X509CertificateLoader.LoadCertificate(root.RawData);
            Assert.True(LicenseChainPolicy.TerminatesAt(chain, rootCopy));
            Assert.False(LicenseChainPolicy.TerminatesAt(chain, unrelatedRoot));
        }
        finally
        {
            LicenseChainPolicy.DisposeChain(chain);
        }
    }

    private static LicenseTrustAnchor TestChainAnchor() => LicenseTrustAnchor.Of(TestLicenseChain.LoadRootCertificate());

    private static void AssertRefused(string compactLicense, LicenseFailureReason expected)
    {
        using var anchor = TestChainAnchor();

        var result = new LicenseVerifier(anchor).Verify(compactLicense, InsideTheGoldenTerm);

        Assert.False(result.IsVerified);
        Assert.Equal(expected, result.FailureReason);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
    }
}
