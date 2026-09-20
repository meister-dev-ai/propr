// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.StaleAddIn;

/// <summary>
///     A driver built against the contract that declares a contract version the host does not carry.
/// </summary>
/// <remarks>
///     Everything below the declaration throws, because a family the loader refuses at the version check is
///     never asked for any of it. If one of these throws in a test, the check did not run where it is supposed
///     to run.
/// </remarks>
public sealed class StaleProviderDriver : IAiProviderDriver
{
    /// <summary>
    ///     Names the environment variable a test sets to change the version this add-in declares.
    /// </summary>
    /// <remarks>
    ///     The declared version is a compile-time constant in a real family, so the two cases the loader
    ///     separates — a family declaring another version and a family declaring none — would otherwise need two
    ///     built assemblies. Read here rather than passed to the loader, because which version a family declares
    ///     is the family's statement and which one the host accepts is not a host setting.
    /// </remarks>
    public const string DeclaredContractVersionVariable = "PROPR_TEST_STALE_ADDIN_CONTRACT_VERSION";

    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "example/stale";

    /// <summary>The one credential shape this family authenticates with.</summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,
        Label = "Stale example provider",
        Version = "1.0",
        ContractVersion = Environment.GetEnvironmentVariable(DeclaredContractVersionVariable) ?? "0.9",
        AuthModes = [new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey])],
        ProtocolModes = new ProviderDeclaredProtocolModes(
        [
            ProviderDeclaredProtocolModes.Auto,
            ProviderVocabulary.Compose(FamilyKey, "ChatCompletions"),
            ProviderDeclaredProtocolModes.Embeddings,
        ]),
        ConformanceInputs = new ProviderConformanceInputs(ApiKeyAuth),
    };

    /// <inheritdoc />
    public ProviderDeclaration Declaration => Declared;


    /// <inheritdoc />
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        throw new NotSupportedException("A family the host refused at load is never asked to validate a target.");
    }

    /// <inheritdoc />
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("A family the host refused at load is never asked to discover models.");
    }

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        throw new NotSupportedException("A family the host refused at load is never asked to verify.");
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        throw new NotSupportedException("A family the host refused at load never serves a call.");
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        throw new NotSupportedException("A family the host refused at load never serves a call.");
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
    {
        throw new NotSupportedException("A family the host refused at load never serves a call.");
    }
}
