// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Reading a stored identity or mode through the spellings a family supersedes, and that keeps its
///     connections resolvable between the moment it declares its new vocabulary and the moment its rows are
///     rewritten.
/// </summary>
/// <remarks>
///     The rules are exercised against declarations written here, so a case states the shape it is about. That
///     every family this product ships resolves its own stored rows through them is asserted over the composed
///     host, which is where all of them are loaded.
/// </remarks>
public sealed class AiProviderLegacyNamesTests
{
    private const string AnthropicKey = "meisterdev/anthropic";

    private const string OpenAiKey = "meisterdev/openAi";

    private const string AnthropicXApiKey = AnthropicKey + ":XApiKey";

    private const string AnthropicMessages = AnthropicKey + ":AnthropicMessages";

    private const string OpenAiApiKey = OpenAiKey + ":ApiKey";

    private const string OpenAiResponses = OpenAiKey + ":Responses";

    [Fact]
    public void AConnectionHoldingAnOldProviderNameResolvesToTheFamilyThatSupersededIt()
    {
        var registry = new AiProviderRegistry([Family("meisterdev/openAi", ["LegacyOpenAi"])]);

        var resolved = registry.ResolveIdentity("LegacyOpenAi");

        Assert.True(resolved.TryGetKey(out var key));
        Assert.Equal("meisterdev/openAi", key);

        // The spelling it was read under is carried beside the key, so a caller writing the value back can leave
        // the row as it is until its own rewrite.
        Assert.Equal("LegacyOpenAi", resolved.Name);
    }

    [Fact]
    public void AConnectionHoldingTheDeclaredKeyResolvesToTheFamilyThatDeclaredIt()
    {
        var registry = new AiProviderRegistry([Family("meisterdev/openAi", ["LegacyOpenAi"])]);

        Assert.True(registry.ResolveIdentity("meisterdev/openAi").TryGetKey(out var byKey));
        Assert.Equal("meisterdev/openAi", byKey);

        // Two spellings differing only in case are one family.
        Assert.True(registry.ResolveIdentity("MeisterDev/OpenAI").TryGetKey(out var byCase));
        Assert.Equal("meisterdev/openAi", byCase);
    }

    // Surrounding space is dropped whichever way the identity resolved. A caller reports it and stores it back,
    // so padding that survived the read would be written into the row and shown to an operator.
    [Theory]
    [InlineData("  meisterdev/openAi  ", "meisterdev/openAi")]
    [InlineData("  someone/elsesFamily  ", "someone/elsesFamily")]
    public void AnIdentityIsCarriedWithoutTheSpaceAroundIt(string stored, string expected)
    {
        var registry = new AiProviderRegistry([Family("meisterdev/openAi", ["LegacyOpenAi"])]);

        Assert.Equal(expected, registry.ResolveIdentity(stored).Name);
    }

    [Fact]
    public void AStoredIdentityMatchingNothingIsReportedUnresolvedRatherThanResolvedToAnything()
    {
        var registry = new AiProviderRegistry([Family("meisterdev/openAi", ["LegacyOpenAi"])]);

        var resolved = registry.ResolveIdentity("someone/elsesFamily");

        Assert.False(resolved.TryGetKey(out _));
        Assert.Equal("someone/elsesFamily", resolved.Name);
    }

    // An absent or blank identity is a column that was never written, which names no family either.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentIdentityIsReportedUnresolved(string? stored)
    {
        var registry = new AiProviderRegistry([Family("meisterdev/openAi", [])]);

        Assert.False(registry.ResolveIdentity(stored).IsResolved);
        Assert.Equal(string.Empty, registry.ResolveIdentity(stored).Name);
    }

    // A caller that has somewhere to put an identity no family claims substitutes one of its own choosing;
    // nothing in the resolution picks one for it.
    [Fact]
    public void AnUnresolvedIdentityTakesTheCallersFallback()
    {
        var registry = new AiProviderRegistry([Family("meisterdev/openAi", [])]);

        Assert.Equal(
            "contoso/llm",
            registry.ResolveIdentity("contoso/llm").KeyOr("contoso/llm"));

        Assert.Equal(
            "meisterdev/openAi",
            registry.ResolveIdentity("meisterdev/openAi").KeyOr("contoso/llm"));
    }

    // A resolution nobody produced reads as unresolved. Without this a caller that forgot to assign one would
    // get a resolved outcome naming an empty family.
    [Fact]
    public void AResolutionThatWasNeverProducedIsUnresolved()
    {
        var resolution = default(AiProviderIdentityResolution);

        Assert.False(resolution.IsResolved);
        Assert.False(resolution.TryGetKey(out _));
        Assert.Equal(string.Empty, resolution.Name);
        Assert.Equal("contoso/llm", resolution.KeyOr("contoso/llm"));
    }

    [Fact]
    public void AnOldUnqualifiedAuthModeResolvesToTheModeTheFamilyDeclaresInItsPlace()
    {
        var registry = new AiProviderRegistry(
        [
            Family(
                "meisterdev/anthropic",
                [],
                authModes: [AnthropicXApiKey],
                authSpellings: new Dictionary<string, string> { [AnthropicXApiKey] = "XApiKey" }),
        ]);

        var resolved = registry.ResolveAuthMode(AnthropicKey, "XApiKey");

        Assert.True(resolved.TryGetValue(out var mode));
        Assert.Equal(AnthropicXApiKey, mode);

        // The spelling it was read under is carried beside the declared one, so a caller writing the value back
        // can leave the row as it is until its own rewrite.
        Assert.Equal("XApiKey", resolved.Name);
    }

    [Fact]
    public void AQualifiedAuthModeResolvesForTheFamilyThatDeclaredItAndForNoOther()
    {
        var registry = new AiProviderRegistry(
        [
            Family(AnthropicKey, [], authModes: [AnthropicXApiKey]),
            Family(OpenAiKey, [], authModes: [OpenAiApiKey]),
        ]);

        Assert.True(registry.ResolveAuthMode(AnthropicKey, AnthropicXApiKey).TryGetValue(out var mine));
        Assert.Equal(AnthropicXApiKey, mine);

        // A value qualified by another family's key belongs to that family, and the row it sits on does not.
        Assert.False(registry.ResolveAuthMode(OpenAiKey, AnthropicXApiKey).TryGetValue(out _));
    }

    [Fact]
    public void AQualifiedModeTheFamilyDoesNotDeclareDoesNotResolve()
    {
        var registry = new AiProviderRegistry(
        [
            Family(OpenAiKey, [], authModes: [OpenAiApiKey]),
        ]);

        Assert.False(registry.ResolveAuthMode(OpenAiKey, OpenAiKey + ":SigV4").TryGetValue(out _));
    }

    [Fact]
    public void AnOldUnqualifiedProtocolModeResolvesToTheModeTheFamilyDeclaresInItsPlace()
    {
        var registry = new AiProviderRegistry(
        [
            Family(
                AnthropicKey,
                [],
                protocolModes: [ProviderDeclaredProtocolModes.Auto, AnthropicMessages],
                protocolSpellings: new Dictionary<string, string>
                {
                    [AnthropicMessages] = "Messages",
                }),
        ]);

        Assert.True(registry.ResolveProtocolMode(AnthropicKey, "Messages").TryGetValue(out var superseded));
        Assert.Equal(AnthropicMessages, superseded);

        Assert.True(registry.ResolveProtocolMode(AnthropicKey, AnthropicMessages).TryGetValue(out var qualified));
        Assert.Equal(AnthropicMessages, qualified);
    }

    // The two host-reserved wire shapes persist unqualified and are owned by nobody, so a family cannot take
    // either over by superseding its spelling.
    [Theory]
    [InlineData(ProviderDeclaredProtocolModes.Auto)]
    [InlineData(ProviderDeclaredProtocolModes.Embeddings)]
    public void AHostReservedProtocolModeResolvesUnqualifiedAndCannotBeShadowed(string reserved)
    {
        var registry = new AiProviderRegistry(
        [
            Family(
                OpenAiKey,
                [],
                protocolModes: [ProviderDeclaredProtocolModes.Auto, OpenAiResponses],
                protocolSpellings: new Dictionary<string, string>
                {
                    [OpenAiResponses] = reserved,
                }),
        ]);

        Assert.True(registry.ResolveProtocolMode(OpenAiKey, reserved).TryGetValue(out var resolved));
        Assert.Equal(reserved, resolved);
    }

    // A reserved shape belongs to no family, so it resolves where the row's family cannot be determined. That
    // is what lets a logical model carrying the default be read before it is mapped to a connection.
    [Theory]
    [InlineData(ProviderDeclaredProtocolModes.Auto)]
    [InlineData(ProviderDeclaredProtocolModes.Embeddings)]
    public void AHostReservedProtocolModeResolvesWithNoFamilyNamed(string reserved)
    {
        var registry = new AiProviderRegistry([Family(OpenAiKey, [])]);

        Assert.True(registry.ResolveProtocolMode(null, reserved).TryGetValue(out var resolved));
        Assert.Equal(reserved, resolved);
    }

    // Two families declaring one mode name declare two shapes, which the qualifier is for. Comparing
    // the mode name alone would serve a request in one family's wire format against the other's endpoint.
    [Fact]
    public void OneModeNameUnderTwoKeysIsTwoDifferentValues()
    {
        const string anthropicResponses = AnthropicKey + ":Responses";

        var registry = new AiProviderRegistry(
        [
            Family(OpenAiKey, [], protocolModes: [ProviderDeclaredProtocolModes.Auto, OpenAiResponses]),
            Family(AnthropicKey, [], protocolModes: [ProviderDeclaredProtocolModes.Auto, anthropicResponses]),
        ]);

        Assert.True(registry.ResolveProtocolMode(OpenAiKey, OpenAiResponses).TryGetValue(out var forOpenAi));
        Assert.True(registry.ResolveProtocolMode(AnthropicKey, anthropicResponses).TryGetValue(out var forAnthropic));

        Assert.NotEqual(forOpenAi, forAnthropic);
        Assert.False(registry.ResolveProtocolMode(OpenAiKey, anthropicResponses).TryGetValue(out _));
    }

    // A mode nothing claims is carried whole, qualifier and all, so a caller reporting it names what the row
    // holds and a caller writing it back leaves the row as it was.
    [Fact]
    public void AStoredModeNoFamilyClaimsIsCarriedAsItWasRead()
    {
        var registry = new AiProviderRegistry([Family(OpenAiKey, [])]);

        var unresolved = registry.ResolveProtocolMode(OpenAiKey, "  contoso/llm:TheirShape  ");

        Assert.False(unresolved.IsResolved);
        Assert.Equal("contoso/llm:TheirShape", unresolved.Name);
        Assert.Equal("Auto", unresolved.ValueOr("Auto"));
    }

    // A resolution nobody produced reads as unresolved, for the reason the identity one does.
    [Fact]
    public void AModeResolutionThatWasNeverProducedIsUnresolved()
    {
        var resolution = default(AiVocabularyResolution);

        Assert.False(resolution.IsResolved);
        Assert.False(resolution.TryGetValue(out _));
        Assert.Equal(string.Empty, resolution.Name);
        Assert.Equal("Auto", resolution.ValueOr("Auto"));
    }

    // A stored identity is the only thing that says which family a row belongs to, so a spelling two families
    // both claim leaves the host nothing to decide with.
    [Fact]
    public void TwoFamiliesSupersedingOneIdentityIsReportedAtLoadNamingBoth()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new AiProviderRegistry(
        [
            Family("meisterdev/openAi", ["SharedLegacyName"]),
            Family("meisterdev/anthropic", ["SharedLegacyName"]),
        ]));

        Assert.Contains("SharedLegacyName", failure.Message, StringComparison.Ordinal);
        Assert.Contains("meisterdev/openAi", failure.Message, StringComparison.Ordinal);
        Assert.Contains("meisterdev/anthropic", failure.Message, StringComparison.Ordinal);
    }

    // A mode sits on a row that already names its family, so two families superseding one spelling is not
    // ambiguous. Every shipped family supersedes 'ApiKey', and refusing that would refuse all of them.
    [Fact]
    public void TwoFamiliesSupersedingOneModeSpellingBothLoadAndResolveTheirOwn()
    {
        var registry = new AiProviderRegistry(
        [
            Family(
                OpenAiKey,
                [],
                authModes: [OpenAiApiKey],
                authSpellings: new Dictionary<string, string> { [OpenAiApiKey] = "SharedModeName" }),
            Family(
                AnthropicKey,
                [],
                authModes: [AnthropicXApiKey],
                authSpellings: new Dictionary<string, string> { [AnthropicXApiKey] = "SharedModeName" }),
        ]);

        Assert.True(registry.ResolveAuthMode(OpenAiKey, "SharedModeName").TryGetValue(out var forOpenAi));
        Assert.Equal(OpenAiApiKey, forOpenAi);

        Assert.True(registry.ResolveAuthMode(AnthropicKey, "SharedModeName").TryGetValue(out var forAnthropic));
        Assert.Equal(AnthropicXApiKey, forAnthropic);
    }

    private static IAiProviderDriver Family(
        string key,
        IReadOnlyList<string> legacyKeys,
        IReadOnlyList<string>? authModes = null,
        IReadOnlyList<string>? protocolModes = null,
        IReadOnlyDictionary<string, string>? authSpellings = null,
        IReadOnlyDictionary<string, string>? protocolSpellings = null)
    {
        authModes ??= [ProviderVocabulary.Compose(key, "ApiKey")];
        protocolModes ??=
            [ProviderDeclaredProtocolModes.Auto, ProviderVocabulary.Compose(key, "ChatCompletions")];

        return new DeclaredOnlyDriver(
            new ProviderDeclaration
            {
                Key = key,
                Label = key,
                Version = "1.0",
                ContractVersion = ProviderContract.Version,
                LegacyNames = ProviderLegacyNames.Create(
                    legacyKeys,
                    authSpellings ?? new Dictionary<string, string>(),
                    protocolSpellings ?? new Dictionary<string, string>()),
                AuthModes =
                [
                    .. authModes.Select(mode =>
                        new ProviderDeclaredAuthMode(mode, [new ProviderCredentialField("apiKey", "Key")])),
                ],
                ProtocolModes = new ProviderDeclaredProtocolModes(protocolModes),
                ConformanceInputs = new ProviderConformanceInputs(authModes[0]),
            });
    }

    // Answers the declaration and nothing else. Resolution reads declarations, so a driver that could build a
    // chat client would add moving parts to a test about names.
    private sealed class DeclaredOnlyDriver(ProviderDeclaration declaration) : IAiProviderDriver
    {
        public ProviderDeclaration Declaration => declaration;

        public string? ValidateProbeTarget(AiProbeTarget target) => null;

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ProviderVerificationResult> VerifyAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default) => throw new NotSupportedException();

        public IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode) => throw new NotSupportedException();

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
