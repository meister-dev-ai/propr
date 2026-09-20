// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Runtime;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Runtime;

/// <summary>
///     A model on a protocol that refuses a sampling temperature does not take one, and the provider refuses the
///     whole request rather than ignoring it. The rule belongs to the pipeline because every stage of a review
///     builds its own request, and it learns from the provider because the recorded capability cannot be trusted
///     to be set. Which families it applies to, and the wire name the refusal keys on, come from what each family
///     declares.
/// </summary>
public sealed class ReasoningModelSamplingDecoratorTests
{
    private const string OpenAiApiKey = "meisterdev/openAi:ApiKey";

    private const string OpenAiChatCompletions = "meisterdev/openAi:ChatCompletions";

    private const string Refusal = "Unsupported parameter: 'temperature' is not supported with this model.";

    // The case every existing installation is in: the model reasons but nothing recorded that it does.
    [Fact]
    public async Task AProviderThatRefusesTheTemperatureIsRetriedWithoutIt()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(endpoint, "meisterdev/openAi", knownToReason: false);

        var response = await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, endpoint.Calls.Count);
        Assert.Equal(0.2f, endpoint.Calls[0].Temperature);
        Assert.Null(endpoint.Calls[1].Temperature);
    }

    // The rejection is paid once, not once per call, so a long review does not double every request.
    [Fact]
    public async Task OnceRefusedTheTemperatureIsOmittedFromEveryLaterCall()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(endpoint, "meisterdev/openAi", knownToReason: false);

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });
        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal(3, endpoint.Calls.Count);
        Assert.Null(endpoint.Calls[2].Temperature);
    }

    // A model recorded as reasoning never pays the first rejection.
    [Fact]
    public async Task AModelKnownToReasonIsSentNoTemperatureAtAll()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(endpoint, "meisterdev/openAi", knownToReason: true);

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Single(endpoint.Calls);
        Assert.Null(endpoint.Calls[0].Temperature);
    }

    // The point of consulting the bundled snapshot: a model it already knows reasons must never spend a call
    // discovering that. An operator should not pay a rejected request to learn what shipped with the product.
    [Fact]
    public async Task AModelTheSnapshotKnowsReasonsIsNeverProbed()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var descriptor = new ProviderModelDescriptor(
            Guid.NewGuid(),
            "gpt-5.6-luna",
            [OpenAiChatCompletions]);
        var client = new ReasoningModelSamplingDecorator(RefusesTemperature("temperature")).Decorate(
            endpoint,
            new ProviderEndpoint("meisterdev/openAi", "https://api.openai.com/v1", OpenAiApiKey, "key"),
            descriptor);

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Single(endpoint.Calls);
        Assert.Null(endpoint.Calls[0].Temperature);
    }

    // Any other rejection must surface as itself. Re-sending it without a temperature would turn one clear
    // failure into two and hide what the provider actually objected to.
    [Fact]
    public async Task AnUnrelatedRejectionIsNotRetried()
    {
        var endpoint = new RefusingChatClient("Unsupported parameter: 'top_p' is not supported with this model.");
        var client = Decorate(endpoint, "meisterdev/openAi", knownToReason: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f }));

        Assert.Single(endpoint.Calls);
    }

    // An ordinary sampling model keeps whatever the operator configured.
    [Fact]
    public async Task AnOrdinarySamplingModelKeepsItsTemperature()
    {
        var endpoint = new RefusingChatClient(refusal: null);
        var client = Decorate(endpoint, "meisterdev/openAi", knownToReason: false);

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal(0.2f, Assert.Single(endpoint.Calls).Temperature);
    }

    // A family that declares no refused temperature is left alone entirely: other vendors accept one from a
    // reasoning-capable model and object only once thinking is on, which their own clients handle.
    [Theory]
    [InlineData("meisterdev/anthropic")]
    [InlineData("meisterdev/awsBedrock")]
    [InlineData("meisterdev/googleVertex")]
    public async Task AFamilyThatDeclaresNoRefusedTemperatureKeepsIt(string providerKind)
    {
        var endpoint = new RefusingChatClient(refusal: null);
        var client = Decorate(endpoint, providerKind, knownToReason: true, driver: RefusesNothing());

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal(0.2f, Assert.Single(endpoint.Calls).Temperature);
    }

    // The spelling is the vendor's, so a family whose endpoint names the parameter differently is recognised by
    // the name it declared and not by the one another vendor uses.
    [Fact]
    public async Task TheRefusalIsRecognisedUnderTheNameTheFamilyDeclared()
    {
        var endpoint = new RefusingChatClient("Unsupported parameter: 'sampling_temperature' is not supported with this model.");
        var client = Decorate(endpoint, "meisterdev/openAiCompatible", knownToReason: false, driver: RefusesTemperature("sampling_temperature"));

        var response = await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, endpoint.Calls.Count);
        Assert.Null(endpoint.Calls[1].Temperature);
    }

    // A family that states its endpoint accepts no temperature has already said what a rejection would teach, so
    // the first call does not pay for it.
    [Fact]
    public async Task AFamilyThatDeclaresItsEndpointAcceptsNoTemperatureIsNeverProbed()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(
            endpoint,
            "meisterdev/openAiCompatible",
            knownToReason: false,
            driver: RefusesTemperature("temperature", acceptsTemperature: false));

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Single(endpoint.Calls);
        Assert.Null(endpoint.Calls[0].Temperature);
    }

    // Options are reused across the turns of one loop, so the caller's instance must come back untouched.
    [Fact]
    public async Task TheCallersOwnOptionsAreNotMutated()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(endpoint, "meisterdev/openAi", knownToReason: false);
        var options = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([Hi()], options);

        Assert.Equal(0.2f, options.Temperature);
    }

    [Fact]
    public void TheRefusalIsRecognisedThroughAnInnerException()
    {
        var wrapped = new InvalidOperationException("call failed", new InvalidOperationException(Refusal));

        Assert.True(ReasoningModelSamplingDecorator.IsTemperatureRefusal(wrapped, "temperature"));
        Assert.False(ReasoningModelSamplingDecorator.IsTemperatureRefusal(new InvalidOperationException("rate limited"), "temperature"));
    }

    private static ChatMessage Hi()
    {
        return new ChatMessage(ChatRole.User, "hi");
    }

    // A distinct model id per client, because a learned refusal is remembered for the whole process and would
    // otherwise carry from one test into the next.
    private static IChatClient Decorate(
        IChatClient inner,
        string providerKind,
        bool knownToReason,
        IAiProviderDriver? driver = null)
    {
        var endpoint = new ProviderEndpoint(providerKind, "https://example.test/v1", OpenAiApiKey, "key");
        var model = new ProviderModelDescriptor(
            Guid.NewGuid(),
            $"a-model-{Guid.NewGuid():N}",
            [OpenAiChatCompletions],
            SupportsReasoning: knownToReason);

        return new ReasoningModelSamplingDecorator(driver ?? RefusesTemperature("temperature"))
            .Decorate(inner, endpoint, model);
    }

    private static IAiProviderDriver RefusesTemperature(string parameterName, bool? acceptsTemperature = null)
    {
        return new DeclaringDriver(
            new ProviderRequestShapeDefaults(
                AcceptsTemperature: acceptsTemperature,
                RefusedParameterNames: new Dictionary<string, ProviderRequestShapeConstraint>(StringComparer.Ordinal)
                {
                    [parameterName] = ProviderRequestShapeConstraint.Temperature,
                }));
    }

    private static IAiProviderDriver RefusesNothing()
    {
        return new DeclaringDriver(ProviderRequestShapeDefaults.Unstated);
    }

    /// <summary>A family that exists only to state a request shape, which is all this stage reads of one.</summary>
    private sealed class DeclaringDriver(ProviderRequestShapeDefaults defaults) : StubDriver
    {
        public override ProviderDeclaration Declaration { get; } = new()
        {
            Key = "test/declaring",
            Label = "Declaring family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("test/declaring:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes(["test/declaring:ChatCompletions"]),
            RequestShapeDefaults = defaults,
            ConformanceInputs = new ProviderConformanceInputs("test/declaring:ApiKey"),
        };
    }

    /// <summary>Refuses any request that carries a temperature, recording every request it is sent.</summary>
    private sealed class RefusingChatClient(string? refusal) : IChatClient
    {
        public List<ChatOptions> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(options ?? new ChatOptions());

            return refusal is not null && options?.Temperature is not null
                ? throw new InvalidOperationException(refusal)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(options ?? new ChatOptions());
            return AsyncEnumerable.Empty<ChatResponseUpdate>();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
