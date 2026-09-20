// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.NonConformingAddIn;

/// <summary>
///     A driver that loads without error and then fails a driver check.
/// </summary>
/// <remarks>
///     It is correct in every way the loader can see: it names the contract version this host carries, it
///     exposes one driver with a parameterless constructor, and its folder ships no copy of a shared assembly.
///     Its declaration is the part that is wrong, and wrong in a way the contract's own constructors accept: a
///     field whose visibility condition names a field the family does not declare. The form would render that
///     field as permanently hidden, or permanently shown, depending on how the condition is read, and nothing
///     about loading the assembly reveals it.
/// </remarks>
public sealed class NonConformingProviderDriver : IAiProviderDriver
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "example/nonconforming";

    /// <summary>The one protocol mode this family owns.</summary>
    public const string ChatCompletionsProtocol = FamilyKey + ":ChatCompletions";

    /// <summary>The one authentication mode this family authenticates with.</summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,
        Label = "Non-conforming example provider",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        AuthModes = [new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey])],
        ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, ChatCompletionsProtocol]),
        ReachedHostPatterns = ["api.nonconforming.example.com"],

        // The mistake: 'useRegion' is declared nowhere, so the condition names nothing and the field it governs
        // can never be shown or never be hidden, depending on which way the host reads an unanswerable condition.
        Fields =
        [
            new ProviderDeclaredField("region", "Region", ProviderFieldKind.String)
            {
                VisibleWhen = new ProviderFieldVisibility("useRegion", "true"),
            },
        ],
        ConformanceInputs = new ProviderConformanceInputs(ApiKeyAuth),
    };

    /// <inheritdoc />
    public ProviderDeclaration Declaration => Declared;


    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProtocolModes => Declared.ProtocolModes.Supported;

    /// <inheritdoc />
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, Declared.ReachedHostPatterns[0], StringComparison.OrdinalIgnoreCase))
        {
            return $"the family is reached at https://{Declared.ReachedHostPatterns[0]}.";
        }

        return target.HasApiKey && ProviderVocabulary.ValuesEqual(target.AuthMode, ApiKeyAuth)
            ? null
            : "the family needs an API key.";
    }

    /// <inheritdoc />
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderModelDiscoveryResult("succeeded", true, [], []));
    }

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // The defect. A client the family built itself carries none of the host's handlers, so the connect-time
        // address check over the operator-supplied endpoint is gone and nothing reports that it is.
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.BaseUrl, UriKind.Absolute) };

        return Task.FromResult(DriverFailureMapper.Verified("Verified."));
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(ProviderEndpoint endpoint, ProviderModelDescriptor model, string protocolMode)
    {
        AiProtocolModeSupport.Require(this.Declaration.Key, this.SupportedProtocolModes, protocolMode);

        return new EmptyChatClient();
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        return ProviderRuntimeCapabilities.None;
    }

    /// <inheritdoc />
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
    {
        throw new InvalidOperationException("The family serves no embedding models.");
    }

    private sealed class EmptyChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "nothing")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "nothing");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
