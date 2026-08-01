// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Runtime;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Runtime;

/// <summary>
///     An OpenAI-family model that reasons does not take a sampling temperature, and the provider refuses the
///     whole request rather than ignoring it. The rule belongs to the pipeline because every stage of a review
///     builds its own request, and it learns from the provider because the recorded capability cannot be trusted
///     to be set.
/// </summary>
public sealed class ReasoningModelSamplingDecoratorTests
{
    private const string Refusal = "Unsupported parameter: 'temperature' is not supported with this model.";

    // The case every existing installation is in: the model reasons but nothing recorded that it does.
    [Fact]
    public async Task AProviderThatRefusesTheTemperatureIsRetriedWithoutIt()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(endpoint, AiProviderKind.OpenAi, knownToReason: false);

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
        var client = Decorate(endpoint, AiProviderKind.OpenAi, knownToReason: false);

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
        var client = Decorate(endpoint, AiProviderKind.OpenAi, knownToReason: true);

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
        var descriptor = new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5.6-luna", [AiProtocolMode.ChatCompletions]);
        var client = new ReasoningModelSamplingDecorator().Decorate(
            endpoint,
            new ProviderEndpoint(AiProviderKind.OpenAi, "https://api.openai.com/v1", AiAuthMode.ApiKey, "key"),
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
        var client = Decorate(endpoint, AiProviderKind.OpenAi, knownToReason: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f }));

        Assert.Single(endpoint.Calls);
    }

    // An ordinary sampling model keeps whatever the operator configured.
    [Fact]
    public async Task AnOrdinarySamplingModelKeepsItsTemperature()
    {
        var endpoint = new RefusingChatClient(refusal: null);
        var client = Decorate(endpoint, AiProviderKind.OpenAi, knownToReason: false);

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal(0.2f, Assert.Single(endpoint.Calls).Temperature);
    }

    // Other vendors accept a temperature from a reasoning-capable model and object only once thinking is on,
    // which their own clients handle, so this stage stays out of their way entirely.
    [Theory]
    [InlineData(AiProviderKind.Anthropic)]
    [InlineData(AiProviderKind.AwsBedrock)]
    [InlineData(AiProviderKind.GoogleVertex)]
    public async Task AnotherVendorKeepsItsTemperature(AiProviderKind providerKind)
    {
        var endpoint = new RefusingChatClient(refusal: null);
        var client = Decorate(endpoint, providerKind, knownToReason: true);

        await client.GetResponseAsync([Hi()], new ChatOptions { Temperature = 0.2f });

        Assert.Equal(0.2f, Assert.Single(endpoint.Calls).Temperature);
    }

    // Options are reused across the turns of one loop, so the caller's instance must come back untouched.
    [Fact]
    public async Task TheCallersOwnOptionsAreNotMutated()
    {
        var endpoint = new RefusingChatClient(Refusal);
        var client = Decorate(endpoint, AiProviderKind.OpenAi, knownToReason: false);
        var options = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([Hi()], options);

        Assert.Equal(0.2f, options.Temperature);
    }

    [Fact]
    public void TheRefusalIsRecognisedThroughAnInnerException()
    {
        var wrapped = new InvalidOperationException("call failed", new InvalidOperationException(Refusal));

        Assert.True(ReasoningModelSamplingDecorator.IsTemperatureRefusal(wrapped));
        Assert.False(ReasoningModelSamplingDecorator.IsTemperatureRefusal(new InvalidOperationException("rate limited")));
    }

    private static ChatMessage Hi()
    {
        return new ChatMessage(ChatRole.User, "hi");
    }

    // A distinct model id per client, because a learned refusal is remembered for the whole process and would
    // otherwise carry from one test into the next.
    private static IChatClient Decorate(IChatClient inner, AiProviderKind providerKind, bool knownToReason)
    {
        var endpoint = new ProviderEndpoint(providerKind, "https://example.test/v1", AiAuthMode.ApiKey, "key");
        var model = new ProviderModelDescriptor(
            Guid.NewGuid(),
            $"a-model-{Guid.NewGuid():N}",
            [AiProtocolMode.ChatCompletions],
            SupportsReasoning: knownToReason);

        return new ReasoningModelSamplingDecorator().Decorate(inner, endpoint, model);
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
