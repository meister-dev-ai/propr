// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Runtime;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Runtime;

/// <summary>
///     A provider serves a conversation's earlier turns from its cache only when the conversation's calls reach the
///     same cache. The pipeline gives every call of one conversation the same routing key, and only a runtime whose
///     driver accepts a routing hint receives one.
/// </summary>
public sealed class PromptCacheRoutingDecoratorTests
{
    // The review loop and the lookup loops resend the start of the conversation on every turn and append to it.
    [Fact]
    public async Task EveryTurnOfOneConversationCarriesTheSameKey()
    {
        var endpoint = new RecordingChatClient();
        var client = Decorate(endpoint, supportsRouting: true);

        await client.GetResponseAsync([System("Review the file."), User("diff of a.cs")]);
        await client.GetResponseAsync(
        [
            System("Review the file."),
            User("diff of a.cs"),
            new ChatMessage(ChatRole.Assistant, "I will read the caller."),
            new ChatMessage(ChatRole.Tool, "caller source"),
        ]);

        var first = ProviderPromptCacheRouting.Read(endpoint.Calls[0]);
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, ProviderPromptCacheRouting.Read(endpoint.Calls[1]));
    }

    [Fact]
    public async Task TwoConversationsCarryDifferentKeys()
    {
        var endpoint = new RecordingChatClient();
        var client = Decorate(endpoint, supportsRouting: true);

        await client.GetResponseAsync([System("Review the file."), User("diff of a.cs")]);
        await client.GetResponseAsync([System("Review the file."), User("diff of b.cs")]);
        await client.GetResponseAsync([System("Verify the claim."), User("diff of a.cs")]);

        var keys = endpoint.Calls.Select(ProviderPromptCacheRouting.Read).ToList();
        Assert.Equal(3, keys.Distinct().Count());
    }

    // A driver that does not report cache routing never sees the entry, so no vendor client can put it on the wire.
    [Fact]
    public async Task ARuntimeWithoutCacheRoutingSendsNoKey()
    {
        var endpoint = new RecordingChatClient();
        var client = Decorate(endpoint, supportsRouting: false);

        await client.GetResponseAsync([System("Review the file."), User("diff of a.cs")]);

        Assert.Same(endpoint, client);
        Assert.Null(ProviderPromptCacheRouting.Read(endpoint.Calls[0]));
    }

    [Fact]
    public async Task AKeyTheCallerSetIsKept()
    {
        var endpoint = new RecordingChatClient();
        var client = Decorate(endpoint, supportsRouting: true);
        var options = new ChatOptions { AdditionalProperties = new() { [ProviderPromptCacheRouting.OptionName] = "caller-key" } };

        await client.GetResponseAsync([System("Review the file."), User("diff of a.cs")], options);

        Assert.Equal("caller-key", ProviderPromptCacheRouting.Read(endpoint.Calls[0]));
    }

    // Callers reuse one options object across the turns of a loop and across loops.
    [Fact]
    public async Task TheCallersOptionsAreLeftUnchanged()
    {
        var endpoint = new RecordingChatClient();
        var client = Decorate(endpoint, supportsRouting: true);
        var options = new ChatOptions { ModelId = "gpt-test" };

        await client.GetResponseAsync([System("Review the file."), User("diff of a.cs")], options);

        Assert.Null(options.AdditionalProperties);
        Assert.Equal("gpt-test", endpoint.Calls[0].ModelId);
        Assert.NotNull(ProviderPromptCacheRouting.Read(endpoint.Calls[0]));
    }

    [Fact]
    public async Task AStreamedCallCarriesTheKeyOfItsConversation()
    {
        var endpoint = new RecordingChatClient();
        var client = Decorate(endpoint, supportsRouting: true);

        await client.GetResponseAsync([System("Review the file."), User("diff of a.cs")]);
        await foreach (var _ in client.GetStreamingResponseAsync([System("Review the file."), User("diff of a.cs")]))
        {
        }

        Assert.Equal(ProviderPromptCacheRouting.Read(endpoint.Calls[0]), ProviderPromptCacheRouting.Read(endpoint.Calls[1]));
    }

    private static IChatClient Decorate(IChatClient endpoint, bool supportsRouting)
    {
        return new PromptCacheRoutingDecorator(supportsRouting).Decorate(
            endpoint,
            new ProviderEndpoint("meisterdev/openAi", "https://api.openai.com/v1", "meisterdev/openAi:ApiKey", "key"),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-test", ["meisterdev/openAi:Responses"]));
    }

    private static ChatMessage System(string text)
    {
        return new ChatMessage(ChatRole.System, text);
    }

    private static ChatMessage User(string text)
    {
        return new ChatMessage(ChatRole.User, text);
    }

    /// <summary>Answers every request and records the options it was sent.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatOptions> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(options ?? new ChatOptions());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
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
