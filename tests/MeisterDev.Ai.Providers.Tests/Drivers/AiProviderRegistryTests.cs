// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     The registry is what makes an open provider enum safe. The enum names families the system can describe;
///     the registry answers which of them this build can actually call, and everything that offers a family to an
///     operator asks the registry rather than the enum.
/// </summary>
public sealed class AiProviderRegistryTests
{
    [Fact]
    public void OnlyRegisteredFamiliesAreReported()
    {
        var registry = new AiProviderRegistry([Driver(AiProviderKind.OpenAi), Driver(AiProviderKind.LiteLlm)]);

        Assert.Equal([AiProviderKind.OpenAi, AiProviderKind.LiteLlm], registry.RegisteredKinds);
        Assert.True(registry.IsRegistered(AiProviderKind.OpenAi));
        Assert.False(registry.IsRegistered(AiProviderKind.Anthropic));
    }

    // Naming a family in the enum registers nothing. This is the property that lets #148 open the enum ahead of
    // F-D/E/F without any of those families becoming selectable.
    [Fact]
    public void AFamilyTheEnumNamesButNoDriverServesIsNotRegistered()
    {
        var registry = new AiProviderRegistry([Driver(AiProviderKind.OpenAiCompatible)]);

        foreach (var unimplemented in new[] { AiProviderKind.Anthropic, AiProviderKind.AwsBedrock, AiProviderKind.GoogleVertex })
        {
            Assert.False(registry.IsRegistered(unimplemented));
            Assert.DoesNotContain(unimplemented, registry.RegisteredKinds);
        }
    }

    // The usual cause of this is a profile configured against a build that has the driver and then run against
    // one that does not, so the message says which families this build does serve.
    [Fact]
    public void AskingForAnUnregisteredFamilyNamesWhatIsAvailable()
    {
        var registry = new AiProviderRegistry([Driver(AiProviderKind.OpenAi)]);

        var failure = Assert.Throws<InvalidOperationException>(() => registry.GetRequired(AiProviderKind.AwsBedrock));

        Assert.Contains("AwsBedrock", failure.Message, StringComparison.Ordinal);
        Assert.Contains("OpenAi", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastRegistrationForAFamilyWins()
    {
        var replacement = Driver(AiProviderKind.OpenAi);

        var registry = new AiProviderRegistry([Driver(AiProviderKind.OpenAi), replacement]);

        Assert.Same(replacement, registry.GetRequired(AiProviderKind.OpenAi));
    }

    private static IAiProviderDriver Driver(AiProviderKind providerKind)
    {
        return new StubDriver(providerKind);
    }

    private sealed class StubDriver(AiProviderKind providerKind) : IAiProviderDriver
    {
        public AiProviderKind ProviderKind => providerKind;

        public IReadOnlyList<AiProtocolMode> SupportedProtocolModes => AiProtocolModeSupport.OpenAiFamily;

        public string? ValidateProbeTarget(AiProbeTarget target) => null;

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IChatClient CreateChatClient(ProviderEndpoint endpoint, ProviderModelDescriptor model, AiProtocolMode protocolMode)
            => throw new NotSupportedException();

        public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            AiProtocolMode protocolMode) => throw new NotSupportedException();

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            AiProtocolMode protocolMode,
            int dimensions) => throw new NotSupportedException();
    }
}
