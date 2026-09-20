// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     The registry is what says which provider families exist. A family is a loaded driver and the identity it
///     declares, so everything that offers a family to an operator asks the registry, and a family the product
///     has never heard of is available as soon as a driver declaring it is composed.
/// </summary>
public sealed class AiProviderRegistryTests
{
    [Fact]
    public void OnlyRegisteredFamiliesAreReported()
    {
        var registry = new AiProviderRegistry([Driver("meisterdev/openAi"), Driver("meisterdev/liteLlm")]);

        Assert.Equal(["meisterdev/liteLlm", "meisterdev/openAi"], registry.RegisteredKinds);
        Assert.True(registry.IsRegistered("meisterdev/openAi"));
        Assert.False(registry.IsRegistered("meisterdev/anthropic"));
    }

    // An identity nothing declares names no family, however well formed it is. There is no second set of
    // identities the product carries, so a family is exactly what was composed.
    [Fact]
    public void AnIdentityNoLoadedDriverDeclaresIsNotRegistered()
    {
        var registry = new AiProviderRegistry([Driver("meisterdev/openAiCompatible")]);

        foreach (var absent in new[] { "meisterdev/anthropic", "meisterdev/awsBedrock", "contoso/llm" })
        {
            Assert.False(registry.IsRegistered(absent));
            Assert.DoesNotContain(absent, registry.RegisteredKinds);
        }
    }

    // A connection written before its family declared its current key holds a spelling that family supersedes,
    // and it has to reach the same driver. The registry answers for every identity a family claims, so a lookup
    // and a resolution cannot disagree about which of them exists.
    [Fact]
    public void AFamilyIsReachedByEverySpellingItClaims()
    {
        var registry = new AiProviderRegistry([Driver("meisterdev/openAi", "LegacyOpenAi")]);

        Assert.True(registry.IsRegistered("LegacyOpenAi"));
        Assert.Same(registry.GetRequired("meisterdev/openAi"), registry.GetRequired("LegacyOpenAi"));

        // The spellings a family supersedes are not identities of their own, so they are not offered anywhere a
        // family is chosen.
        Assert.Equal(["meisterdev/openAi"], registry.RegisteredKinds);
    }

    // Case is folded wherever two keys are compared, so a value read back in another casing names the same
    // family rather than none.
    [Fact]
    public void ALookupIgnoresCaseAndSurroundingSpace()
    {
        var registry = new AiProviderRegistry([Driver("meisterdev/openAi")]);

        Assert.True(registry.IsRegistered("MeisterDev/OpenAI"));
        Assert.True(registry.IsRegistered("  meisterdev/openAi  "));
    }

    [Fact]
    public void AnAbsentIdentityIsNotRegistered()
    {
        var registry = new AiProviderRegistry([Driver("meisterdev/openAi")]);

        Assert.False(registry.IsRegistered(null));
        Assert.False(registry.IsRegistered(string.Empty));
        Assert.False(registry.IsRegistered("   "));
    }

    // The usual cause of this is a profile configured against a build that has the driver and then run against
    // one that does not, so the message says which families this build does serve. The exact exception type is
    // part of that: an unregistered family is refused by the registry, not reported as a failed dictionary
    // lookup that names nothing.
    [Fact]
    public void AskingForAnUnregisteredFamilyNamesWhatIsAvailable()
    {
        var registry = new AiProviderRegistry([Driver("meisterdev/openAi")]);

        var failure = Assert.Throws<InvalidOperationException>(() => registry.GetRequired("meisterdev/awsBedrock"));

        Assert.Contains("meisterdev/awsBedrock", failure.Message, StringComparison.Ordinal);
        Assert.Contains("meisterdev/openAi", failure.Message, StringComparison.Ordinal);
    }

    // Keeping either of two drivers that claim one identity leaves the family served by a driver nobody
    // selected, with nothing recorded to say the other was displaced. The registry refuses to be built instead,
    // so the composition error surfaces where the registry is resolved and not on the first review that needs
    // the family. The key is what a connection is stored against, so a second claim on it would take over every
    // connection already stored.
    [Fact]
    public void TwoDriversDeclaringOneIdentityAreRefusedAndNamedBoth()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new AiProviderRegistry([new StubDriver("shared/key"), new RivalDriver("shared/key")]));

        Assert.Contains("shared/key", failure.Message, StringComparison.Ordinal);

        // Two types, so the message naming both is something this can tell apart from it naming one twice.
        Assert.Contains(typeof(StubDriver).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(RivalDriver).FullName!, failure.Message, StringComparison.Ordinal);
    }

    // The key is compared ignoring case wherever it is read, so two spellings that differ only in case are one
    // family. Indexing them apart would serve whichever casing a caller happened to use.
    [Fact]
    public void TwoSpellingsOfOneKeyThatDifferOnlyByCaseAreOneFamily()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AiProviderRegistry([new StubDriver("shared/key"), new RivalDriver("Shared/Key")]));
    }

    // A family whose driver reads no credential shape cannot be configured: every mode an operator could pick
    // would be one the driver ignores. The registry refuses to be built, so the composition error surfaces where
    // the registry is resolved rather than on the first profile saved against that family. The message names the
    // driver type because the family alone does not say which registration to correct.
    [Fact]
    public void ADriverThatDeclaresNoCredentialShapeIsRefused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new AiProviderRegistry([new ShapelessDriver("meisterdev/openAi")]));

        Assert.Contains(typeof(ShapelessDriver).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Contains("meisterdev/openAi", failure.Message, StringComparison.Ordinal);
    }

    // A mode with no field declaration is a mode nothing is collected for: the console offers it, renders no
    // input, and a profile is saved with no credential that then fails verification. Refused where the
    // composition is built, and the message names the mode so the missing declaration can be found.
    [Fact]
    public void ADriverThatDeclaresAModeWithoutItsFieldsIsRefused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new AiProviderRegistry([new FieldlessDriver("meisterdev/openAi")]));

        Assert.Contains(typeof(FieldlessDriver).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Contains("meisterdev/openAi:SigV4", failure.Message, StringComparison.Ordinal);
    }

    // A mode present as a key with nothing behind it is the same defect as a missing key, and worse to diagnose:
    // the declaration passes the key check and every caller that reads the fields is handed a null list.
    [Fact]
    public void ADriverThatDeclaresAModeWithANullFieldListIsRefused()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            new AiProviderRegistry([new NullFieldListDriver("meisterdev/openAi")]));

        Assert.Contains(typeof(NullFieldListDriver).FullName!, failure.Message, StringComparison.Ordinal);
        Assert.Contains("meisterdev/openAi:ApiKey", failure.Message, StringComparison.Ordinal);
    }

    private static IAiProviderDriver Driver(string key, params string[] supersededKeys)
    {
        return new StubDriver(key, supersededKeys);
    }

    /// <summary>A second driver type, so a duplicate registration names two types and not one twice.</summary>
    private sealed class RivalDriver(string key) : StubDriver(key);

    private sealed class ShapelessDriver(string key) : StubDriver(key)
    {
        public override IReadOnlyList<string> SupportedAuthModes => [];
    }

    /// <summary>A driver that offers a mode without saying what has to be entered for it.</summary>
    private sealed class FieldlessDriver(string key) : StubDriver(key)
    {
        private readonly string _key = key;

        public override IReadOnlyList<string> SupportedAuthModes =>
            [ProviderVocabulary.Compose(this._key, "ApiKey"), ProviderVocabulary.Compose(this._key, "SigV4")];
    }

    /// <summary>A driver that names a mode in its declaration with no list of fields behind it.</summary>
    private sealed class NullFieldListDriver(string key) : StubDriver(key)
    {
        private readonly string _key = key;

        public override IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
            new Dictionary<string, IReadOnlyList<ProviderCredentialField>>
            {
                [ProviderVocabulary.Compose(this._key, "ApiKey")] = null!,
            };
    }

    private class StubDriver(string key, params string[] supersededKeys) : IAiProviderDriver
    {
        public virtual ProviderDeclaration Declaration { get; } = new()
        {
            Key = key,
            Label = key,
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            LegacyNames = ProviderLegacyNames.Create(
                supersededKeys,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()),
            AuthModes = [new ProviderDeclaredAuthMode(ProviderVocabulary.Compose(key, "ApiKey"), [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes(
            [
                ProviderDeclaredProtocolModes.Auto,
                ProviderVocabulary.Compose(key, "Responses"),
                ProviderVocabulary.Compose(key, "ChatCompletions"),
                ProviderDeclaredProtocolModes.Embeddings,
            ]),
            ConformanceInputs = new ProviderConformanceInputs(ProviderVocabulary.Compose(key, "ApiKey")),
        };

        public IReadOnlyList<string> SupportedProtocolModes =>
        [
            ProviderDeclaredProtocolModes.Auto,
            ProviderVocabulary.Compose(key, "Responses"),
            ProviderVocabulary.Compose(key, "ChatCompletions"),
            ProviderDeclaredProtocolModes.Embeddings,
        ];

        public virtual IReadOnlyList<string> SupportedAuthModes => AiAuthModeSupport.ApiKeyOnly(key);

        public virtual IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields
            => this.Declaration.CredentialFields;

        public string? ValidateProbeTarget(AiProbeTarget target) => null;

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IChatClient CreateChatClient(ProviderEndpoint endpoint, ProviderModelDescriptor model, string protocolMode)
            => throw new NotSupportedException();

        public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode) => throw new NotSupportedException();

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode,
            int dimensions) => throw new NotSupportedException();
    }
}
