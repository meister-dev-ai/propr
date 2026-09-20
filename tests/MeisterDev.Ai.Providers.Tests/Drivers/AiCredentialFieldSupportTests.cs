// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     What a host is told when the credential it collected is not the one the family reads.
/// </summary>
/// <remarks>
///     The host knows only that a value is absent or that a name is not in the declaration, so the wording is
///     decided here and reads the same whichever family produced it. What each message has to carry is the field
///     name the driver reads back by, because that is what an operator has to correct.
/// </remarks>
public sealed class AiCredentialFieldSupportTests
{
    private const string BedrockKey = "meisterdev/awsBedrock";

    private const string BedrockSigV4 = BedrockKey + ":SigV4";

    private const string AzureKey = "meisterdev/azureOpenAi";

    private const string AzureIdentity = AzureKey + ":AzureIdentity";

    private static readonly IReadOnlyList<ProviderCredentialField> AccessKey =
    [
        new ProviderCredentialField("accessKeyId", "Access key ID", IsSecret: false),
        new ProviderCredentialField("secretAccessKey", "Secret access key"),
        new ProviderCredentialField("sessionToken", "Session token", IsRequired: false),
    ];

    [Fact]
    public void ARequiredFieldLeftEmptyIsRefusedNamingIt()
    {
        var refusals = AiCredentialFieldSupport.FindCredentialFieldRefusals(
            BedrockKey,
            BedrockSigV4,
            AccessKey,
            ProviderCredentialValues.From(new Dictionary<string, string> { ["accessKeyId"] = "AKIAEXAMPLE" }));

        var refusal = Assert.Single(refusals);
        Assert.Contains("secretAccessKey", refusal, StringComparison.Ordinal);
        Assert.Contains(BedrockKey, refusal, StringComparison.Ordinal);
        Assert.Contains(BedrockSigV4, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionalFieldLeftEmptyIsAccepted()
    {
        var refusals = AiCredentialFieldSupport.FindCredentialFieldRefusals(
            BedrockKey,
            BedrockSigV4,
            AccessKey,
            ProviderCredentialValues.From(
                new Dictionary<string, string>
                {
                    ["accessKeyId"] = "AKIAEXAMPLE",
                    ["secretAccessKey"] = "secret",
                }));

        Assert.Empty(refusals);
    }

    // A name the family does not declare is a value the operator believes is in use and nothing reads. Storing
    // it silently would leave a credential in the row that no call ever sends.
    [Fact]
    public void AFieldTheFamilyDidNotDeclareIsRefusedNamingIt()
    {
        var refusals = AiCredentialFieldSupport.FindCredentialFieldRefusals(
            BedrockKey,
            BedrockSigV4,
            AccessKey,
            ProviderCredentialValues.From(
                new Dictionary<string, string>
                {
                    ["accessKeyId"] = "AKIAEXAMPLE",
                    ["secretAccessKey"] = "secret",
                    ["region"] = "eu-central-1",
                }));

        var refusal = Assert.Single(refusals);
        Assert.Contains("region", refusal, StringComparison.Ordinal);
        Assert.Contains("accessKeyId", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AModeThatTakesNoCredentialRefusesAnythingSupplied()
    {
        var refusals = AiCredentialFieldSupport.FindCredentialFieldRefusals(
            AzureKey,
            AzureIdentity,
            AiCredentialFieldSupport.None,
            ProviderCredentialValues.Single(ProviderSecretEnvelope.ApiKeyField, "a-key"));

        var refusal = Assert.Single(refusals);
        Assert.Contains(ProviderSecretEnvelope.ApiKeyField, refusal, StringComparison.Ordinal);
        Assert.Contains(AzureIdentity, refusal, StringComparison.Ordinal);
    }

    // A refusal is shown on a form and copied into logs, so it names fields and never repeats what was entered
    // into them.
    [Fact]
    public void ARefusalNeverQuotesAValue()
    {
        const string entered = "super-secret-material";

        var refusals = AiCredentialFieldSupport.FindCredentialFieldRefusals(
            BedrockKey,
            BedrockSigV4,
            AccessKey,
            ProviderCredentialValues.From(
                new Dictionary<string, string>
                {
                    ["accessKeyId"] = entered,
                    ["notAField"] = entered,
                }));

        Assert.NotEmpty(refusals);
        Assert.All(refusals, refusal => Assert.DoesNotContain(entered, refusal, StringComparison.Ordinal));
    }

    // A credential of one field is stored as its value alone, so the name it was written under is gone by the
    // time it is read back. Without this, editing a Vertex profile's display name would refuse the save because
    // the carried-forward credential arrived under the wrong name.
    [Fact]
    public void ASingleStoredFieldIsAdoptedUnderTheDeclaredName()
    {
        var adopted = AiCredentialFieldSupport.Adopt(
            [new ProviderCredentialField("serviceAccountJson", "Service account key (JSON)")],
            ProviderCredentialValues.Single(ProviderSecretEnvelope.ApiKeyField, "{\"type\":\"service_account\"}"));

        Assert.Equal("{\"type\":\"service_account\"}", Assert.Contains("serviceAccountJson", adopted));
    }

    // Several values cannot be matched to several names by position, so a multi-field credential is left as it
    // was stored and refused if it does not fit — which should happen when the mode was changed without
    // the credential being re-entered.
    [Fact]
    public void SeveralStoredFieldsAreLeftAsTheyWere()
    {
        var stored = ProviderCredentialValues.From(new Dictionary<string, string> { ["accessKeyId"] = "AKIA", ["secretAccessKey"] = "secret" });

        Assert.Same(stored, AiCredentialFieldSupport.Adopt([AiCredentialFieldSupport.ApiKey], stored));
    }

    // Only the generic single-value name is adopted. A value that came back under a name a driver declared is
    // that family's material, and renaming it into a different field would present it as another family's
    // credential: a Vertex service-account document moved under 'apiKey' is sent to the Gemini surface in a
    // header, private key included.
    [Fact]
    public void AStoredFieldUnderADeclaredNameIsNeverRenamedIntoAnotherField()
    {
        var stored = ProviderCredentialValues.Single(
            "serviceAccountJson",
            "{\"type\":\"service_account\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\"}");

        Assert.Same(stored, AiCredentialFieldSupport.Adopt([AiCredentialFieldSupport.ApiKey], stored));
    }

    [Fact]
    public void AStoredFieldAlreadyUnderTheDeclaredNameIsLeftAlone()
    {
        var stored = ProviderCredentialValues.Single(ProviderSecretEnvelope.ApiKeyField, "a-key");

        Assert.Same(stored, AiCredentialFieldSupport.Adopt([AiCredentialFieldSupport.ApiKey], stored));
    }

    // The fields a shape collects are read off the authentication mode that declares it, so a shape the family
    // does not declare has none. That case is refused separately, by AiAuthModeSupport, which names it.
    [Fact]
    public void AModeWithNoDeclarationTakesNoFields()
    {
        var declaration = OneApiKeyShape(BedrockKey);

        Assert.Empty(declaration.CredentialFieldsFor(BedrockSigV4));
        Assert.Equal([AiCredentialFieldSupport.ApiKey], declaration.CredentialFieldsFor(BedrockKey + ":ApiKey"));
    }

    private static ProviderDeclaration OneApiKeyShape(string key)
    {
        return new ProviderDeclaration
        {
            Key = key,
            Label = key,
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes =
            [
                new ProviderDeclaredAuthMode(
                    ProviderVocabulary.Compose(key, "ApiKey"),
                    [AiCredentialFieldSupport.ApiKey]),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs(ProviderVocabulary.Compose(key, "ApiKey")),
        };
    }
}
