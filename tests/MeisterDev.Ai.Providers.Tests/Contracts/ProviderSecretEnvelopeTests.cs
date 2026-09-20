// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     Covers the stored shape of a credential. The envelope exists so a provider needing three credential fields
///     does not force a schema change or an undocumented separator, and so rows written before it existed keep
///     working — both of which are properties of the decoder rather than of any one provider.
/// </summary>
public sealed class ProviderSecretEnvelopeTests
{
    // Credential shapes as they persist: the declaring family's key joined to the mode name. The envelope reads
    // no mode name of its own, so one family's shapes stand for any family's here.
    private const string ApiKeyShape = "example/provider:ApiKey";

    private const string AzureIdentityShape = "example/provider:AzureIdentity";

    private const string SigV4Shape = "example/provider:SigV4";

    private const string GcpAdcShape = "example/provider:GcpAdc";

    private const string XApiKeyShape = "example/provider:XApiKey";

    [Fact]
    public void AnApiKeyRoundTripsThroughTheEnvelope()
    {
        var encoded = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-secret-value").Encode();

        var decoded = ProviderSecretEnvelope.Decode(encoded, ApiKeyShape);

        Assert.Equal("sk-secret-value", decoded.SingleValue);
        Assert.Equal(ApiKeyShape, decoded.Mode);
        Assert.Equal(ProviderSecretEnvelope.CurrentVersion, decoded.Version);
    }

    [Fact]
    public void AMultiFieldCredentialKeepsEveryField()
    {
        var envelope = new ProviderSecretEnvelope(
            ApiKeyShape,
            new Dictionary<string, string>
            {
                ["accessKeyId"] = "AKIA-example",
                ["secretAccessKey"] = "secret-part",
                ["sessionToken"] = "session-part",
            });

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal(3, decoded.Fields.Count);
        Assert.Equal("secret-part", decoded.Fields["secretAccessKey"]);
        // More than one field means there is no single value to hand a driver expecting one string.
        Assert.Null(decoded.SingleValue);
    }

    // Every credential stored before the envelope existed is a bare string. Reading those has to keep working
    // without a data migration, or adopting the envelope would silently invalidate configured profiles.
    [Fact]
    public void ABareStringIsReadAsTheSingleFieldCredentialItIs()
    {
        var decoded = ProviderSecretEnvelope.Decode("legacy-raw-key", ApiKeyShape);

        Assert.Equal("legacy-raw-key", decoded.SingleValue);
        Assert.Equal(ApiKeyShape, decoded.Mode);
    }

    // A key can legitimately start with a brace, and a JSON service-account document certainly does. Neither may
    // be mistaken for a malformed envelope and lost.
    [Theory]
    [InlineData("{not json at all")]
    [InlineData("{\"type\":\"service_account\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\"}")]
    public void SomethingThatIsNotAnEnvelopeIsStillTreatedAsACredential(string stored)
    {
        var decoded = ProviderSecretEnvelope.Decode(stored, ApiKeyShape);

        Assert.Equal(stored, decoded.SingleValue);
    }

    [Fact]
    public void NoCredentialYieldsNoFields()
    {
        Assert.Empty(ProviderSecretEnvelope.Decode(null, AzureIdentityShape).Fields);
        Assert.Empty(ProviderSecretEnvelope.Decode("   ", AzureIdentityShape).Fields);
        Assert.Null(ProviderSecretEnvelope.ForApiKey(ApiKeyShape, null).SingleValue);
    }

    // The row records what the credential was created for. A profile whose auth mode was edited without the
    // credential being re-entered must not read the old material as though it fits the new mode.
    [Fact]
    public void TheStoredModeWinsOverTheCallersExpectation()
    {
        var encoded = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-secret-value").Encode();

        var decoded = ProviderSecretEnvelope.Decode(encoded, AzureIdentityShape);

        Assert.Equal(ApiKeyShape, decoded.Mode);
    }

    // A credential that is not a single string is what the envelope exists for, so it has to carry every field
    // intact and keep the mode that was stored.
    [Theory]
    [InlineData(SigV4Shape)]
    [InlineData(GcpAdcShape)]
    [InlineData(XApiKeyShape)]
    public void TheNewAuthModesRoundTripWithTheirFields(string mode)
    {
        var envelope = new ProviderSecretEnvelope(
            mode,
            new Dictionary<string, string> { ["accessKeyId"] = "AKIA-example", ["secretAccessKey"] = "secret-part" });

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal(mode, decoded.Mode);
        Assert.Equal("secret-part", decoded.Fields["secretAccessKey"]);
    }

    [Fact]
    public void RenderingTheEnvelopeNamesItsFieldsAndNeverTheirValues()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-secret-value");

        var rendered = envelope.ToString();

        Assert.DoesNotContain("sk-secret-value", rendered, StringComparison.Ordinal);
        Assert.Contains(ProviderSecretEnvelope.ApiKeyField, rendered, StringComparison.Ordinal);
    }

    // When a credential stops being usable is a property of the credential, so it travels inside the envelope
    // and the schema stays free of it.
    [Fact]
    public void TheExpiryRoundTripsWithTheCredential()
    {
        var expiry = new DateTimeOffset(2026, 9, 12, 10, 30, 0, TimeSpan.Zero);
        var envelope = new ProviderSecretEnvelope(
            ApiKeyShape,
            new Dictionary<string, string> { ["accessToken"] = "a-token" })
        {
            ExpiresAt = expiry,
        };

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal(expiry, decoded.ExpiresAt);
    }

    // A row written before the expiry existed carries none, and reads back saying so rather than inventing one.
    // That is also what an operator-entered key carries: nothing states when such a key stops working.
    [Fact]
    public void ACredentialStoredWithoutAnExpiryReadsBackWithoutOne()
    {
        var decoded = ProviderSecretEnvelope.Decode(
            """{"v":1,"mode":"ApiKey","fields":{"apiKey":"sk-older-row"}}""",
            ApiKeyShape);

        Assert.Null(decoded.ExpiresAt);
        Assert.Equal("sk-older-row", decoded.Fields["apiKey"]);
    }

    // A family's secret-marked declared values are held here too, with the key of the family that wrote them, so
    // a value one family stored is not read as another's under the same field name.
    [Fact]
    public void ADeclaredSecretRoundTripsWithTheFamilyThatWroteIt()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-key") with
        {
            DeclaredSecrets = new Dictionary<string, string> { ["clientSecret"] = "declared-value" },
            IdentityKey = "meisterdev/example",
        };

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal("declared-value", decoded.DeclaredSecrets["clientSecret"]);
        Assert.Equal("meisterdev/example", decoded.IdentityKey);
        Assert.Equal("sk-key", decoded.SingleValue);
    }

    // The credential and the declared values are two namespaces inside one envelope: one family naming both
    // `apiKey` would otherwise store one over the other, and the single-value credential would stop reading back
    // as one.
    [Fact]
    public void ADeclaredSecretSharingACredentialFieldNameKeepsBothValues()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-credential") with
        {
            DeclaredSecrets = new Dictionary<string, string> { [ProviderSecretEnvelope.ApiKeyField] = "declared-value" },
            IdentityKey = "meisterdev/example",
        };

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal("sk-credential", decoded.SingleValue);
        Assert.Equal("declared-value", decoded.DeclaredSecrets[ProviderSecretEnvelope.ApiKeyField]);
    }

    [Fact]
    public void ARowWrittenBeforeDeclaredValuesExistedCarriesNone()
    {
        var decoded = ProviderSecretEnvelope.Decode(
            """{"v":1,"mode":"ApiKey","fields":{"apiKey":"sk-older-row"}}""",
            ApiKeyShape);

        Assert.Empty(decoded.DeclaredSecrets);
        Assert.Null(decoded.IdentityKey);
    }

    [Fact]
    public void RenderingTheEnvelopeNamesItsDeclaredFieldsAndNeverTheirValues()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-key") with
        {
            DeclaredSecrets = new Dictionary<string, string> { ["clientSecret"] = "declared-value" },
            IdentityKey = "meisterdev/example",
        };

        var rendered = envelope.ToString();

        Assert.DoesNotContain("declared-value", rendered, StringComparison.Ordinal);
        Assert.Contains("clientSecret", rendered, StringComparison.Ordinal);
    }

    // The whole envelope is keyed by the family that wrote it, not only its declared values: a credential field
    // means whatever the family that wrote it says it means, and one mode name already keys several incompatible
    // payload shapes.
    [Fact]
    public void AnEnvelopeRecordsTheFamilyThatWroteItAndIsReadBackByThatFamily()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-key") with
        {
            IdentityKey = "meisterdev/googleVertex",
        };

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal("meisterdev/googleVertex", decoded.IdentityKey);
        Assert.True(decoded.WrittenBy("meisterdev/googleVertex"));
        Assert.Null(decoded.DescribeForeignRead("meisterdev/googleVertex"));

        // Two spellings differing only in case are one family.
        Assert.True(decoded.WrittenBy("MeisterDev/GoogleVertex"));
    }

    [Fact]
    public void ReadingAnEnvelopeAsAnotherFamilyIsRefusedNamingBoth()
    {
        var decoded = ProviderSecretEnvelope.Decode(
            (ProviderSecretEnvelope.ForApiKey(ApiKeyShape, "sk-key") with
            {
                IdentityKey = "meisterdev/googleVertex",
            }).Encode(),
            ApiKeyShape);

        Assert.False(decoded.WrittenBy("meisterdev/anthropic"));

        var refusal = decoded.DescribeForeignRead("meisterdev/anthropic");

        Assert.NotNull(refusal);
        Assert.Contains("meisterdev/googleVertex", refusal, StringComparison.Ordinal);
        Assert.Contains("meisterdev/anthropic", refusal, StringComparison.Ordinal);

        // Naming the families is not naming the credential.
        Assert.DoesNotContain("sk-key", refusal, StringComparison.Ordinal);
    }

    // An envelope written before the key was recorded belongs to whichever family holds the row. Refusing it
    // would lock an operator out of a credential that for several families is the only copy.
    [Fact]
    public void AnEnvelopeWrittenBeforeTheKeyWasRecordedIsReadableAndTakesTheKeyOnTheNextWrite()
    {
        var decoded = ProviderSecretEnvelope.Decode(
            """{"v":1,"mode":"ApiKey","fields":{"apiKey":"sk-older-row"}}""",
            ApiKeyShape);

        Assert.Null(decoded.IdentityKey);
        Assert.True(decoded.WrittenBy("meisterdev/anthropic"));
        Assert.Null(decoded.DescribeForeignRead("meisterdev/anthropic"));

        var rewritten = ProviderSecretEnvelope.Decode(
            (decoded with { IdentityKey = "meisterdev/anthropic" }).Encode(),
            ApiKeyShape);

        Assert.Equal("meisterdev/anthropic", rewritten.IdentityKey);
        Assert.Equal("sk-older-row", rewritten.SingleValue);
    }

    // The key was recorded against the declared values before it governed the whole envelope, and it named the
    // same thing, so a row written then reads back with the family it was written by.
    [Fact]
    public void AnEnvelopeCarryingTheEarlierSpellingOfTheKeyReadsBackWithIt()
    {
        var decoded = ProviderSecretEnvelope.Decode(
            """
            {"v":1,"mode":"ApiKey","fields":{"apiKey":"sk-key"},
             "declared":{"clientSecret":"declared-value"},"declaredBy":"meisterdev/example"}
            """,
            ApiKeyShape);

        Assert.Equal("meisterdev/example", decoded.IdentityKey);
        Assert.Equal("declared-value", decoded.DeclaredSecrets["clientSecret"]);
    }

    // The mode persists as the name it is stored under, so a qualified spelling survives the round trip and
    // still wins over what the caller expected.
    [Fact]
    public void AQualifiedModeNameRoundTripsAndStillWinsOverTheCallers()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey("meisterdev/googleVertex:gcpAdc", "{\"type\":\"service_account\"}");

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), ApiKeyShape);

        Assert.Equal("meisterdev/googleVertex:gcpAdc", decoded.Mode);
    }
}
