// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.TwoFamilyAddIn;

/// <summary>
///     The first of the two families this assembly exposes.
/// </summary>
/// <remarks>
///     Neither family is reached: the loader refuses the assembly before it constructs anything, which 
///     this add-in exists to exercise. Each member therefore answers with the least it can and the call paths
///     throw, so a change that let one of them be constructed is visible rather than quietly served.
/// </remarks>
public sealed class FirstFamilyProviderDriver : DeclaredOnlyProviderDriver
{
    /// <inheritdoc />
    public override ProviderDeclaration Declaration { get; } = Declare("example/first");
}

/// <summary>The second of the two families this assembly exposes.</summary>
public sealed class SecondFamilyProviderDriver : DeclaredOnlyProviderDriver
{
    /// <inheritdoc />
    public override ProviderDeclaration Declaration { get; } = Declare("example/second");
}

/// <summary>
///     What both families in this assembly have in common: a well-formed declaration and no working call path.
/// </summary>
public abstract class DeclaredOnlyProviderDriver : IAiProviderDriver
{
    /// <inheritdoc />
    public abstract ProviderDeclaration Declaration { get; }


    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProtocolModes => this.Declaration.ProtocolModes.Supported;

    /// <inheritdoc />
    public string? ValidateProbeTarget(AiProbeTarget target) => null;

    /// <inheritdoc />
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default) => throw new NotSupportedException(Unreachable);

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default) => throw new NotSupportedException(Unreachable);

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode) => throw new NotSupportedException(Unreachable);

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode) => ProviderRuntimeCapabilities.None;

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions) => throw new NotSupportedException(Unreachable);

    /// <summary>A declaration the loader would accept, for a family it refuses the assembly over.</summary>
    /// <param name="key">The identity key this family declares.</param>
    protected static ProviderDeclaration Declare(string key)
    {
        return new ProviderDeclaration
        {
            Key = key,
            Label = $"Example family {key}",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode(ProviderVocabulary.Compose(key, "ApiKey"), [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes(
            [
                ProviderDeclaredProtocolModes.Auto,
                ProviderVocabulary.Compose(key, "ChatCompletions"),
            ]),
            ConformanceInputs = new ProviderConformanceInputs(ProviderVocabulary.Compose(key, "ApiKey")),
        };
    }

    private const string Unreachable =
        "The loader refuses an assembly exposing more than one provider family, so neither of this assembly's "
        + "families is ever reached.";
}
