// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MeisterDev.ProPR.Licensing.Tests;

public sealed class LicenseRefusalTests
{
    private static readonly DateTimeOffset CertificateNotBefore = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CertificateNotAfter = new(2039, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A newer license is not a broken license. Reporting it as malformed would send an operator looking for
    // a corrupt file instead of a product upgrade, so it gets its own reason.
    [Fact]
    public void AHigherSchemaVersion_IsRefusedAsUnsupported()
    {
        var compact = RawLicense.SignWithClaim("schemaVersion", (LicenseDocument.SchemaVersion + 1).ToString());

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(LicenseFailureReason.UnsupportedSchemaVersion, result.FailureReason);
    }

    [Fact]
    public void TheWriterRefusesASchemaVersionTheReaderDoesNotSupport()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            TestLicenseChain.GoldenClaims() with { SchemaVersion = LicenseDocument.SchemaVersion + 1 },
            signingKey,
            [signer]));
    }

    [Fact]
    public void TheWriterRefusesNullCollectionsAndAnIssueInstantBeforeTheUnixEpoch()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var claims = TestLicenseChain.GoldenClaims();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { Capabilities = null! }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { Capabilities = [null!] }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { Limits = null! }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            claims with
            {
                IssuedAt = DateTimeOffset.UnixEpoch.AddSeconds(-1),
                NotBefore = DateTimeOffset.UnixEpoch,
            },
            signingKey,
            [signer]));
    }

    [Theory]
    [InlineData("\"0\"")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void ASchemaVersionThatIsNotAPositiveInteger_IsMalformed(string json)
    {
        AssertMalformed(RawLicense.SignWithClaim("schemaVersion", json));
    }

    // The header names the algorithm, so a reader that followed that name would let the document decide how
    // it is checked, including choosing not to be checked at all.
    [Theory]
    [InlineData("RS256")]
    [InlineData("none")]
    [InlineData("ES384")]
    [InlineData("HS256")]
    public void AnAlgorithmOtherThanEs256_IsMalformed(string algorithm)
    {
        var compact = RawLicense.Sign(RawLicense.HeaderJson(algorithm), RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        AssertMalformed(compact);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("jti")]
    [InlineData("licensee")]
    [InlineData("iat")]
    [InlineData("nbf")]
    [InlineData("exp")]
    [InlineData("capabilities")]
    public void AMissingRequiredClaim_IsMalformed(string name)
    {
        var claims = RawLicense.AcceptedClaims();
        claims.Remove(name);

        AssertMalformed(RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims)));
    }

    [Theory]
    [InlineData("jti", "\"\"")]
    [InlineData("jti", "42")]
    [InlineData("licensee", "\"   \"")]
    [InlineData("licensee", "null")]
    [InlineData("iat", "\"1767225600\"")]
    [InlineData("exp", "1767225600.5")]
    [InlineData("capabilities", "\"review\"")]
    [InlineData("capabilities", "[\"review\",7]")]
    public void AClaimOfTheWrongShape_IsMalformed(string name, string json)
    {
        AssertMalformed(RawLicense.SignWithClaim(name, json));
    }

    [Theory]
    [InlineData(1767225600, 2082758400, 2082758400)]
    [InlineData(1767225600, 2082758401, 2082758400)]
    public void AClaimTermThatDoesNotBeginBeforeItEnds_IsMalformed(long issuedAt, long notBefore, long expiresAt)
    {
        var claims = RawLicense.AcceptedClaims();
        claims["iat"] = issuedAt.ToString();
        claims["nbf"] = notBefore.ToString();
        claims["exp"] = expiresAt.ToString();

        AssertMalformed(RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims)));
    }

    [Fact]
    public void AClaimTermThatCannotRepresentTheGraceWindow_IsMalformed()
    {
        AssertMalformed(RawLicense.SignWithClaim("exp", DateTimeOffset.MaxValue.ToUnixTimeSeconds().ToString()));
    }

    [Theory]
    [InlineData("jti", 257)]
    [InlineData("licensee", 513)]
    public void AnIdentityClaimLongerThanItsStoredBound_IsMalformed(string name, int length)
    {
        AssertMalformed(RawLicense.SignWithClaim(name, JsonSerializer.Serialize(new string('x', length))));
    }

    [Theory]
    [InlineData("jti")]
    [InlineData("licensee")]
    public void AnIdentityClaimWithAControlCharacter_IsMalformed(string name)
    {
        AssertMalformed(RawLicense.SignWithClaim(name, JsonSerializer.Serialize("text\nwith-break")));
    }

    // The licensee is repeated into a single line of command output, into activation history and into the
    // admin UI. A line or paragraph separator ends a line that has to stay one, a bidi override reverses what
    // is rendered after it, and a zero-width character or byte-order mark is not visible at all.
    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    [InlineData('\u200B')]
    [InlineData('\uFEFF')]
    [InlineData('\u202E')]
    [InlineData('\u200F')]
    public void AnIdentityClaimWithAFormattingOrSeparatorCharacter_IsMalformed(char character)
    {
        AssertMalformed(RawLicense.SignWithClaim("licensee", JsonSerializer.Serialize("Contoso" + character + "Ltd")));
        AssertMalformed(RawLicense.SignWithClaim("jti", JsonSerializer.Serialize("id" + character + "6c2a4d1e")));
        AssertMalformed(RawLicense.SignWithClaim("capabilities", JsonSerializer.Serialize(new[] { "rev" + character + "iew" })));
    }

    [Theory]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    [InlineData('\u200B')]
    [InlineData('\uFEFF')]
    [InlineData('\u202E')]
    [InlineData('\u200F')]
    public void TheWriterRefusesAnIdentityWithAFormattingOrSeparatorCharacter(char character)
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var claims = TestLicenseChain.GoldenClaims();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { Licensee = "Contoso" + character + "Ltd" }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { LicenseId = "id" + character + "6c2a4d1e" }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            claims with { Capabilities = ["rev" + character + "iew"] },
            signingKey,
            [signer]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-license")]
    [InlineData("header.payload")]
    [InlineData("header.payload.signature.extra")]
    [InlineData("a.b.")]
    [InlineData(".b.c")]
    public void ADocumentThatIsNotThreeSegments_IsMalformed(string compact)
    {
        AssertMalformed(compact);
    }

    [Fact]
    public void ASegmentThatIsNotBase64Url_IsMalformed()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());
        var segments = compact.Split('.');

        AssertMalformed(string.Join('.', "not base64url!", segments[1], segments[2]));
    }

    // The decoder's own validity check passes "AA=" and then raises when asked to decode it, so a document
    // shaped like this reached the caller as an exception instead of a refusal.
    [Fact]
    public void APaddedSegmentTheDecoderCallsValid_IsMalformedRatherThanThrowing()
    {
        AssertMalformed("AA=.AAAA.AAAA");
        Assert.Equal(LicenseFailureReason.Malformed, LicenseReader.Parse("AA=.AAAA.AAAA").FailureReason);
    }

    // Padding and whitespace are the two ways the same license could arrive in more than one byte form. Both
    // are refused, so a license has one encoding and comparing two documents means comparing two strings.
    [Theory]
    [InlineData("=")]
    [InlineData("==")]
    [InlineData("\n")]
    [InlineData(" ")]
    [InlineData("+")]
    [InlineData("/")]
    public void ASignatureSegmentCarryingCharactersOutsideTheAlphabet_IsMalformed(string addition)
    {
        var segments = TestLicenseChain.ReadGoldenLicense().Split('.');

        AssertMalformed(string.Join('.', segments[0], segments[1], segments[2] + addition));
        AssertMalformed(string.Join('.', segments[0], segments[1], Insert(segments[2], addition)));
    }

    // The document is refused on length alone, before its structure, signature or claims are looked at, so an
    // oversized document that would otherwise read cleanly is still refused.
    [Fact]
    public void ADocumentLongerThanTheCeiling_IsMalformed()
    {
        var claims = RawLicense.AcceptedClaims();
        claims["licensee"] = JsonSerializer.Serialize(new string('x', LicenseDocument.MaximumLength));
        var oversized = RawLicense.Sign(RawLicense.HeaderJson(), RawLicense.PayloadJson(claims));

        Assert.True(oversized.Length > LicenseDocument.MaximumLength);
        AssertMalformed(oversized);
    }

    [Fact]
    public void ASignatureOfTheWrongLength_IsMalformed()
    {
        var compact = TestLicenseChain.Sign(TestLicenseChain.GoldenClaims());
        var segments = compact.Split('.');

        AssertMalformed(string.Join('.', segments[0], segments[1], Base64Url.EncodeToString(new byte[32])));
    }

    [Theory]
    [InlineData("{\"typ\":\"propr-license+jws\"}")]
    [InlineData("{\"alg\":\"ES256\"}")]
    [InlineData("{\"alg\":\"ES256\",\"x5c\":[]}")]
    [InlineData("{\"alg\":\"ES256\",\"x5c\":\"not-an-array\"}")]
    [InlineData("{\"alg\":\"ES256\",\"x5c\":[\"bm90LWEtY2VydGlmaWNhdGU=\"]}")]
    [InlineData("[\"not\",\"an\",\"object\"]")]
    public void AHeaderWithoutAUsableCertificateChain_IsMalformed(string headerJson)
    {
        AssertMalformed(RawLicense.Sign(headerJson, RawLicense.PayloadJson(RawLicense.AcceptedClaims())));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string payload\"")]
    public void APayloadThatIsNotAJsonObject_IsMalformed(string payloadJson)
    {
        AssertMalformed(RawLicense.Sign(RawLicense.HeaderJson(), payloadJson));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheWriterRefusesAnEmptyIdentity(string blank)
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims() with { Licensee = blank }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims() with { LicenseId = blank }, signingKey, [signer]));
    }

    [Fact]
    public void TheWriterRefusesAClaimTermThatDoesNotBeginBeforeItEndsOrCannotHoldTheGraceWindow()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var claims = TestLicenseChain.GoldenClaims();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { ExpiresAt = claims.NotBefore }, signingKey, [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            claims with { ExpiresAt = claims.NotBefore.AddSeconds(-1) },
            signingKey,
            [signer]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims with { ExpiresAt = DateTimeOffset.MaxValue }, signingKey, [signer]));
    }

    // The document carries the term at whole-second precision. A term that orders only through sub-second
    // parts would sign here and then be refused by every reader re-checking the written seconds.
    [Fact]
    public void TheWriterRefusesATermThatOnlyOrdersThroughSubSecondParts()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var claims = TestLicenseChain.GoldenClaims();
        var collapses = claims with
        {
            NotBefore = claims.IssuedAt.AddMilliseconds(400),
            ExpiresAt = claims.IssuedAt.AddMilliseconds(900),
        };

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(collapses, signingKey, [signer]));
    }

    [Theory]
    [InlineData(257, 32)]
    [InlineData(32, 513)]
    public void TheWriterRefusesAnIdentityLongerThanItsStoredBound(int licenseIdLength, int licenseeLength)
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            TestLicenseChain.GoldenClaims() with
            {
                LicenseId = new string('i', licenseIdLength),
                Licensee = new string('e', licenseeLength),
            },
            signingKey,
            [signer]));
    }

    [Theory]
    [InlineData("license id")]
    [InlineData("licensee")]
    public void TheWriterRefusesAnIdentityWithAControlCharacter(string identity)
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var claims = identity == "license id"
            ? TestLicenseChain.GoldenClaims() with { LicenseId = "id\nwith-break" }
            : TestLicenseChain.GoldenClaims() with { Licensee = "licensee\nwith-break" };

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims, signingKey, [signer]));
    }

    [Fact]
    public void TheWriterRefusesAHeaderWithoutASigningCertificate()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, []));
    }

    // Every certificate in the chain is written into the header, so an absent one past the signer has to be
    // refused as an unusable argument rather than reaching serialization.
    [Fact]
    public void TheWriterRefusesAChainCarryingAnAbsentCertificate()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [signer, null!]));
        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [null!]));
    }

    // A capability name reaches the product's capability set, so the writer holds it to the same rule as the
    // other text claims.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("review\nrunners")]
    [InlineData("review\trunners")]
    public void TheWriterRefusesACapabilityThatIsNotABoundedControlFreeName(string capability)
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            TestLicenseChain.GoldenClaims() with { Capabilities = ["review", capability] },
            signingKey,
            [signer]));
    }

    [Fact]
    public void TheWriterRefusesACapabilityLongerThanItsBound()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            TestLicenseChain.GoldenClaims() with { Capabilities = [new string('c', 129)] },
            signingKey,
            [signer]));
    }

    // The reader applies the same rule, so a name the writer would not produce does not reach the capability
    // set through a document assembled elsewhere.
    [Theory]
    [InlineData("[\"\"]")]
    [InlineData("[\"   \"]")]
    [InlineData("[\"review\",\"runners\\nrunners\"]")]
    public void ACapabilityThatIsNotABoundedControlFreeName_IsMalformed(string json)
    {
        AssertMalformed(RawLicense.SignWithClaim("capabilities", json));
    }

    [Fact]
    public void ACapabilityLongerThanItsBound_IsMalformed()
    {
        AssertMalformed(RawLicense.SignWithClaim("capabilities", JsonSerializer.Serialize(new[] { new string('c', 129) })));
    }

    // Refusing at issuance keeps the mismatch from becoming a document that readers refuse after it has been
    // handed to a customer.
    [Fact]
    public void TheWriterRefusesAKeyOnAnotherCurve()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [signer]));
    }

    [Fact]
    public void TheWriterRefusesAKeyOnAnother256BitCurve()
    {
        using var signingKey = ECDsa.Create(ECCurve.CreateFromValue("1.3.132.0.10"));
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [signer]));
    }

    [Fact]
    public void TheWriterRefusesAKeyThatDoesNotMatchTheSigningCertificate()
    {
        using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), unrelatedKey, [signer]));
    }

    // The header says ES256, so a signing certificate holding any other kind of key describes a signature no
    // ES256 signer could have produced.
    [Fact]
    public void ASigningCertificateWithAnRsaKey_IsMalformed()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Wrong Key Type", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(CertificateNotBefore, CertificateNotAfter);

        AssertMalformed(RawLicense.Sign(RawLicense.HeaderJson([certificate]), RawLicense.PayloadJson(RawLicense.AcceptedClaims())));
    }

    [Fact]
    public void ASigningCertificateOnAnother256BitCurve_IsMalformed()
    {
        using var key = ECDsa.Create(ECCurve.CreateFromValue("1.3.132.0.10"));
        var request = new CertificateRequest("CN=Wrong 256-Bit Curve", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(CertificateNotBefore, CertificateNotAfter);

        AssertMalformed(RawLicense.SignWith(key, RawLicense.HeaderJson([certificate]), RawLicense.PayloadJson(RawLicense.AcceptedClaims())));
    }

    [Theory]
    [InlineData("nistP384")]
    [InlineData("nistP521")]
    public void ASigningCertificateOnAnotherCurve_IsMalformed(string curveName)
    {
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName(curveName));
        var request = new CertificateRequest("CN=Wrong Curve", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(CertificateNotBefore, CertificateNotAfter);

        AssertMalformed(RawLicense.Sign(RawLicense.HeaderJson([certificate]), RawLicense.PayloadJson(RawLicense.AcceptedClaims())));
    }

    // The certificate parses, so the reader gets as far as reading its key, and a curve identifier nothing
    // implements raises there. The document comes from outside, so it has to be refused rather than the
    // exception escaping the reader.
    [Fact]
    public void ASigningCertificateNamingAnUnknownCurve_IsMalformedRatherThanThrowing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = GeneratedLicenseChain.CreateCertificateNamingAnUnknownCurve();
        var compact = RawLicense.SignWith(
            key,
            RawLicense.HeaderJson([certificate]),
            RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        AssertMalformed(compact);
        Assert.Equal(LicenseFailureReason.Malformed, LicenseReader.Parse(compact).FailureReason);
    }

    // The method reports an unusable argument as ArgumentException, so a certificate whose key cannot be read
    // has to arrive that way rather than as the exception the platform raises.
    [Fact]
    public void TheWriterRefusesASigningCertificateNamingAnUnknownCurve()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var certificate = GeneratedLicenseChain.CreateCertificateNamingAnUnknownCurve();

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(TestLicenseChain.GoldenClaims(), signingKey, [certificate]));
    }

    // Each claim is bounded on its own, which does not bound what they compose into. A long capability list
    // produces a document a reader refuses on length before it looks at anything else, so issuance has to
    // refuse it rather than hand out a document whose refusal points the operator at the file.
    [Fact]
    public void TheWriterRefusesClaimsThatComposePastTheDocumentCeiling()
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var capabilities = Enumerable.Range(0, 500)
            .Select(index => new string('c', 120) + index.ToString("D8", CultureInfo.InvariantCulture))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(
            TestLicenseChain.GoldenClaims() with { Capabilities = capabilities },
            signingKey,
            [signer]));

        Assert.Contains(LicenseDocument.MaximumLength.ToString(CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
    }

    // Certificate validity is evaluated at the instant the document states as its issue time. An instant
    // outside the signing certificate's own window is signed as stated and then refused everywhere as an
    // untrusted signer, a diagnostic that names the signer and not the instant that caused it.
    [Theory]
    [InlineData(1609372800)]
    [InlineData(2177539200)]
    public void TheWriterRefusesAnIssueInstantOutsideTheSigningCertificatesValidityWindow(long issuedAtUnixSeconds)
    {
        using var signingKey = TestLicenseChain.LoadSigningKey();
        using var signer = TestLicenseChain.LoadSigningCertificate();
        var claims = TestLicenseChain.GoldenClaims() with { IssuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtUnixSeconds) };

        Assert.Throws<ArgumentException>(() => LicenseWriter.Sign(claims, signingKey, [signer]));
    }

    // The two instants above are chosen to sit one day outside the fixture signing certificate's window, so
    // that window is read back here and the numbers stay meaningful if the fixture is regenerated.
    [Fact]
    public void TheFixtureSigningCertificate_BoundsTheInstantsTheIssueWindowTestUses()
    {
        using var signer = TestLicenseChain.LoadSigningCertificate();

        Assert.Equal(CertificateNotBefore, new DateTimeOffset(signer.NotBefore.ToUniversalTime(), TimeSpan.Zero));
        Assert.Equal(CertificateNotAfter, new DateTimeOffset(signer.NotAfter.ToUniversalTime(), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("\"application/jwt\"")]
    [InlineData("\"propr-license\"")]
    [InlineData("\"PROPR-LICENSE+JWS\"")]
    [InlineData("7")]
    public void AHeaderStatingAnotherDocumentType_IsMalformed(string typeJson)
    {
        AssertMalformed(RawLicense.Sign(RawLicense.HeaderJsonWithType(typeJson), RawLicense.PayloadJson(RawLicense.AcceptedClaims())));
    }

    // The type identifies the format for a reader that has several; it is not what establishes this document,
    // so a header that omits it is still read.
    [Fact]
    public void AHeaderWithoutADocumentType_IsAccepted()
    {
        var compact = RawLicense.Sign(RawLicense.HeaderJsonWithType(null), RawLicense.PayloadJson(RawLicense.AcceptedClaims()));

        using var publicKey = TestLicenseChain.LoadSignerPublicKey();
        var result = LicenseReader.Read(compact, publicKey);

        Assert.True(result.IsSuccess, result.FailureDetail);
        result.License.Dispose();
    }

    private static void AssertMalformed(string compactLicense)
    {
        using var publicKey = TestLicenseChain.LoadSignerPublicKey();

        var result = LicenseReader.Read(compactLicense, publicKey);

        Assert.False(result.IsSuccess);
        Assert.Equal(LicenseFailureReason.Malformed, result.FailureReason);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
    }

    private static string Insert(string segment, string addition)
        => segment[..(segment.Length / 2)] + addition + segment[(segment.Length / 2)..];
}
