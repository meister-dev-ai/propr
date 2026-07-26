// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     Covers the stored shape of a credential. The envelope exists so a provider needing three credential fields
///     does not force a schema change or an undocumented separator, and so rows written before it existed keep
///     working — both of which are properties of the decoder rather than of any one provider.
/// </summary>
public sealed class ProviderSecretEnvelopeTests
{
    [Fact]
    public void AnApiKeyRoundTripsThroughTheEnvelope()
    {
        var encoded = ProviderSecretEnvelope.ForApiKey(AiAuthMode.ApiKey, "sk-secret-value").Encode();

        var decoded = ProviderSecretEnvelope.Decode(encoded, AiAuthMode.ApiKey);

        Assert.Equal("sk-secret-value", decoded.SingleValue);
        Assert.Equal(AiAuthMode.ApiKey, decoded.Mode);
        Assert.Equal(ProviderSecretEnvelope.CurrentVersion, decoded.Version);
    }

    [Fact]
    public void AMultiFieldCredentialKeepsEveryField()
    {
        var envelope = new ProviderSecretEnvelope(
            AiAuthMode.ApiKey,
            new Dictionary<string, string>
            {
                ["accessKeyId"] = "AKIA-example",
                ["secretAccessKey"] = "secret-part",
                ["sessionToken"] = "session-part",
            });

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), AiAuthMode.ApiKey);

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
        var decoded = ProviderSecretEnvelope.Decode("legacy-raw-key", AiAuthMode.ApiKey);

        Assert.Equal("legacy-raw-key", decoded.SingleValue);
        Assert.Equal(AiAuthMode.ApiKey, decoded.Mode);
    }

    // A key can legitimately start with a brace, and a JSON service-account document certainly does. Neither may
    // be mistaken for a malformed envelope and lost.
    [Theory]
    [InlineData("{not json at all")]
    [InlineData("{\"type\":\"service_account\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\"}")]
    public void SomethingThatIsNotAnEnvelopeIsStillTreatedAsACredential(string stored)
    {
        var decoded = ProviderSecretEnvelope.Decode(stored, AiAuthMode.ApiKey);

        Assert.Equal(stored, decoded.SingleValue);
    }

    [Fact]
    public void NoCredentialYieldsNoFields()
    {
        Assert.Empty(ProviderSecretEnvelope.Decode(null, AiAuthMode.AzureIdentity).Fields);
        Assert.Empty(ProviderSecretEnvelope.Decode("   ", AiAuthMode.AzureIdentity).Fields);
        Assert.Null(ProviderSecretEnvelope.ForApiKey(AiAuthMode.ApiKey, null).SingleValue);
    }

    // The row records what the credential was created for. A profile whose auth mode was edited without the
    // credential being re-entered must not read the old material as though it fits the new mode.
    [Fact]
    public void TheStoredModeWinsOverTheCallersExpectation()
    {
        var encoded = ProviderSecretEnvelope.ForApiKey(AiAuthMode.ApiKey, "sk-secret-value").Encode();

        var decoded = ProviderSecretEnvelope.Decode(encoded, AiAuthMode.AzureIdentity);

        Assert.Equal(AiAuthMode.ApiKey, decoded.Mode);
    }

    // The auth modes opened by #148 are exactly the ones whose credential is not a single string, so the envelope
    // has to carry them intact and keep the mode that was stored.
    [Theory]
    [InlineData(AiAuthMode.SigV4)]
    [InlineData(AiAuthMode.GcpAdc)]
    [InlineData(AiAuthMode.XApiKey)]
    public void TheNewAuthModesRoundTripWithTheirFields(AiAuthMode mode)
    {
        var envelope = new ProviderSecretEnvelope(
            mode,
            new Dictionary<string, string> { ["accessKeyId"] = "AKIA-example", ["secretAccessKey"] = "secret-part" });

        var decoded = ProviderSecretEnvelope.Decode(envelope.Encode(), AiAuthMode.ApiKey);

        Assert.Equal(mode, decoded.Mode);
        Assert.Equal("secret-part", decoded.Fields["secretAccessKey"]);
    }

    [Fact]
    public void RenderingTheEnvelopeNamesItsFieldsAndNeverTheirValues()
    {
        var envelope = ProviderSecretEnvelope.ForApiKey(AiAuthMode.ApiKey, "sk-secret-value");

        var rendered = envelope.ToString();

        Assert.DoesNotContain("sk-secret-value", rendered, StringComparison.Ordinal);
        Assert.Contains(ProviderSecretEnvelope.ApiKeyField, rendered, StringComparison.Ordinal);
    }
}
