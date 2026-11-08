// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

// The reasoning effort of Chat Completions is marked for evaluation only.
#pragma warning disable OPENAI001

namespace MeisterDev.Ai.Providers.OpenAiCompatibleAddIn.Tests;

/// <summary>
///     What a reasoning request in provider-neutral terms puts on the wire of Chat Completions, read from the recorded
///     request bodies.
/// </summary>
/// <remarks>
///     OpenAI options that the host builds are a foreign type for the copy of the OpenAI library this add-in loads, so
///     the host hands a client of this family the neutral request. Chat Completions receives only the level none,
///     because a server whose model does not know the field can refuse the request, and from GPT-5.4 on OpenAI refuses
///     function tools there together with any other effort.
/// </remarks>
public sealed class OpenAiCompatibleReasoningRequestTests
{
    private const string ChatCompletionBody =
        """
        {
          "id": "chatcmpl-fake", "object": "chat.completion", "created": 1770000000, "model": "m",
          "choices": [{ "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "hi" } }],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;

    private const string BaseUrl = "https://opencode.ai/zen/v1";

    private const string RemoteModelId = "kimi-k2";

    private static readonly ProviderModelDescriptor Model = new(
        Guid.NewGuid(),
        RemoteModelId,
        [ProviderDeclaredProtocolModes.Auto, OpenAiCompatibleProviderDriver.ChatCompletionsProtocol]);

    // The host hands the neutral request only to a native-protocol client, and reads the protocol from it.
    [Fact]
    public void TheClient_IsANativeProtocolClientOfChatCompletions()
    {
        using var chat = new OpenAiCompatibleProviderDriver().CreateChatClient(
            EndpointOn(new RecordingEndpoint()),
            Model,
            ProviderDeclaredProtocolModes.Auto);

        Assert.Equal(
            OpenAiCompatibleProviderDriver.ChatCompletionsProtocol,
            Assert.IsAssignableFrom<INativeProtocolChatClient>(chat).NativeProtocol);
    }

    [Fact]
    public async Task TheLevelNone_ReachesChatCompletions()
    {
        var body = await SendAsync(Reasoning(ProviderReasoningEffort.None, false, "none"));

        Assert.Equal("none", body.GetProperty("reasoning_effort").GetString());
    }

    // A server whose model does not know the field can refuse the request, and from GPT-5.4 on OpenAI refuses function
    // tools on Chat Completions together with any effort other than none. A request that names no level is not sent
    // its neutral effort either.
    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    [InlineData(null)]
    public async Task AnyOtherLevel_LeavesChatCompletionsWithoutAnEffort(string? level)
    {
        var body = await SendAsync(Reasoning(ProviderReasoningEffort.High, true, level));

        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // A caller that also states an effort on the shared setting, for example because it took the protocol of another
    // model, still sends Chat Completions no effort other than none.
    [Fact]
    public async Task ASharedEffortOtherThanNone_DoesNotReachChatCompletions()
    {
        var options = Reasoning(ProviderReasoningEffort.High, false, "high");
        options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High };

        var body = await SendAsync(options);

        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // Without a neutral request the options reach the adapter as the caller built them, including options of the
    // OpenAI library that a caller in the same load context builds itself.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionsWithoutANeutralRequest_ReachTheAdapterUnchanged(bool callerBuildsLibraryOptions)
    {
        var options = new ChatOptions
        {
            MaxOutputTokens = 512,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
            RawRepresentationFactory = callerBuildsLibraryOptions
                ? _ => new ChatCompletionOptions { ReasoningEffortLevel = ChatReasoningEffortLevel.Low }
                : null,
        };

        var sent = await SendAsync(options);
        var sentByTheAdapterAlone = await SendThroughTheAdapterAloneAsync(options);

        Assert.Equal(sentByTheAdapterAlone.GetRawText(), sent.GetRawText());
    }

    /// <summary>Asks for reasoning the way the host does: neutrally, and only of a native-protocol client.</summary>
    private static ChatOptions Reasoning(ProviderReasoningEffort effort, bool captureReasoning, string? level)
    {
        return new ChatOptions
        {
            RawRepresentationFactory = client => client is INativeProtocolChatClient
                ? new ProviderReasoningRequest(effort, captureReasoning) { Level = level }
                : null,
        };
    }

    // Sends one call through a client of this family and returns the body of the request it sent.
    private static async Task<JsonElement> SendAsync(ChatOptions options)
    {
        var wire = new RecordingEndpoint();
        using var chat = new OpenAiCompatibleProviderDriver().CreateChatClient(EndpointOn(wire), Model, ProviderDeclaredProtocolModes.Auto);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")], options);

        return JsonDocument.Parse(Assert.Single(wire.Bodies)).RootElement;
    }

    // Sends the same call through the Microsoft.Extensions.AI adapter without this family's client around it.
    private static async Task<JsonElement> SendThroughTheAdapterAloneAsync(ChatOptions options)
    {
        var wire = new RecordingEndpoint();
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(BaseUrl),
            Transport = new HttpClientPipelineTransport(new HttpClient(wire)),
        };
        using var chat = new ChatClient(RemoteModelId, new ApiKeyCredential("zen-key"), clientOptions).AsIChatClient();

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")], options);

        return JsonDocument.Parse(Assert.Single(wire.Bodies)).RootElement;
    }

    private static ProviderEndpoint EndpointOn(HttpMessageHandler wire)
    {
        return new ProviderEndpoint("meisterdev/openAiCompatible", BaseUrl, OpenAiCompatibleProviderDriver.ApiKeyAuth, "zen-key")
        {
            HostContext = new FakeProviderHostContext(wire),
        };
    }

    /// <summary>Records the body of each request and answers with a completed chat completion.</summary>
    private sealed class RecordingEndpoint : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatCompletionBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
