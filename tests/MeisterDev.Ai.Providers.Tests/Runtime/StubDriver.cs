// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Runtime;

/// <summary>
///     A family that states the minimum a declaration requires and nothing a pipeline stage does not read.
/// </summary>
/// <remarks>
///     The stages under test take a driver for one answer each: a request shape, a usage mapping. Refusing the
///     rest makes a stage that started calling something else fail visibly instead of taking a silent default.
/// </remarks>
public abstract class StubDriver : IAiProviderDriver
{
    /// <inheritdoc />
    public virtual ProviderDeclaration Declaration { get; } = new()
    {
        Key = "test/stub",
        Label = "Stub family",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        AuthModes = [new ProviderDeclaredAuthMode("test/stub:ApiKey", [AiCredentialFieldSupport.ApiKey])],
        ProtocolModes = new ProviderDeclaredProtocolModes(["test/stub:ChatCompletions"]),
        ConformanceInputs = new ProviderConformanceInputs("test/stub:ApiKey"),
    };


    /// <inheritdoc />
    public virtual ProviderTokenUsage ReadUsage(UsageDetails? usage) => ProviderTokenUsage.FromUsageDetails(usage);

    /// <inheritdoc />
    public string? ValidateProbeTarget(AiProbeTarget target) => null;

    /// <inheritdoc />
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public IChatClient CreateChatClient(ProviderEndpoint endpoint, ProviderModelDescriptor model, string protocolMode)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
        => ProviderRuntimeCapabilities.None;

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
        => throw new NotSupportedException();
}
