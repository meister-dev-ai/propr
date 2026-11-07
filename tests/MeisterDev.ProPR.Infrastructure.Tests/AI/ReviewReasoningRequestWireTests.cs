// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     The reasoning request as the OpenAI adapters write it to the wire. Chat Completions and the Responses API read
///     the request from different places, so each case is sent through both real adapters against a fake endpoint.
/// </summary>
public sealed class ReviewReasoningRequestWireTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NoReasoning_ReachesAModelThatAcceptsNone_AsTheEffortNone(bool responsesApi, bool resolved)
    {
        var options = resolved
            ? new ChatOptions().ApplyResolvedReasoning(false, ReviewReasoningEffort.None, supportsReasoningEffortNone: true)
            : new ChatOptions().ApplyReasoning(false, ReviewReasoningEffort.None, supportsReasoningEffortNone: true);

        var request = await SendAsync(responsesApi, options);

        Assert.Equal("none", SentEffort(request, responsesApi));
    }

    // A model that refuses an explicit none, such as an older reasoning model, keeps the request it was sent
    // before: no reasoning parameter, so the model's own default applies.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NoReasoning_SendsNoReasoningParameter_ToAModelWithoutNone(bool responsesApi, bool resolved)
    {
        var options = resolved
            ? new ChatOptions().ApplyResolvedReasoning(false, ReviewReasoningEffort.None)
            : new ChatOptions().ApplyReasoning(false, ReviewReasoningEffort.None);

        var request = await SendAsync(responsesApi, options);

        Assert.False(request.RootElement.TryGetProperty(responsesApi ? "reasoning" : "reasoning_effort", out _));
    }

    // A configured effort reaches the Responses API and stays off Chat Completions, where OpenAI refuses function tools
    // with any effort other than none from GPT-5.4 on. This holds for an ordinary review pass and for a resolved
    // decision, which relayed calls and the verification calls use. Whether the model accepts the effort none changes
    // nothing for these efforts.
    [Theory]
    [InlineData(ReviewReasoningEffort.Low, "low", false)]
    [InlineData(ReviewReasoningEffort.Medium, "medium", false)]
    [InlineData(ReviewReasoningEffort.High, "high", false)]
    [InlineData(ReviewReasoningEffort.Low, "low", true)]
    [InlineData(ReviewReasoningEffort.Medium, "medium", true)]
    [InlineData(ReviewReasoningEffort.High, "high", true)]
    public async Task AConfiguredEffort_ReachesTheResponsesApiAndNotChatCompletions(
        ReviewReasoningEffort effort,
        string expected,
        bool resolved)
    {
        ChatOptions Options() => resolved
            ? new ChatOptions().ApplyResolvedReasoning(false, effort, supportsReasoningEffortNone: true)
            : new ChatOptions().ApplyReasoning(false, effort, supportsReasoningEffortNone: true);

        using var responses = await SendAsync(responsesApi: true, Options());
        using var chatCompletions = await SendAsync(responsesApi: false, Options());

        Assert.Equal(expected, SentEffort(responses, responsesApi: true));
        Assert.False(chatCompletions.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    // A runner job sends its effort over the relay, and the control plane rebuilds the request with the same rule.
    [Theory]
    [InlineData("low", null)]
    [InlineData("medium", false)]
    [InlineData("high", true)]
    public async Task ARelayedEffort_ReachesTheResponsesApiAndNotChatCompletions(string wireEffort, bool? resolvedCapture)
    {
        ChatOptions? Options() => RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(ReasoningEffort: wireEffort),
            resolvedCapture);

        using var responses = await SendAsync(responsesApi: true, Options());
        using var chatCompletions = await SendAsync(responsesApi: false, Options());

        Assert.Equal(wireEffort, SentEffort(responses, responsesApi: true));
        Assert.False(chatCompletions.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    // The summary opt-in and the effort are separate settings, so asking for the summary of a model told not to
    // reason still sends none.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoReasoningWithASummaryRequest_StillSendsTheEffortNone(bool responsesApi)
    {
        var options = new ChatOptions().ApplyReasoning(true, ReviewReasoningEffort.None, supportsReasoningEffortNone: true);

        var request = await SendAsync(responsesApi, options);

        Assert.Equal("none", SentEffort(request, responsesApi));
        if (responsesApi)
        {
            Assert.Equal("auto", request.RootElement.GetProperty("reasoning").GetProperty("summary").GetString());
        }
    }

    // A runner states the effort none on the wire only for a model its manifest marks as accepting it, so the
    // control plane sends none for such a request.
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    public async Task ARelayedEffortNone_ReachesBothOpenAiTransports(bool responsesApi, bool? resolvedCapture)
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(ReasoningEffort: "none"),
            resolvedCapture);

        var request = await SendAsync(responsesApi, options);

        Assert.Equal("none", SentEffort(request, responsesApi));
    }

    private static string? SentEffort(JsonDocument request, bool responsesApi)
    {
        return responsesApi
            ? request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString()
            : request.RootElement.GetProperty("reasoning_effort").GetString();
    }

    private static async Task<JsonDocument> SendAsync(bool responsesApi, ChatOptions? options)
    {
        using var endpoint = new RecordingEndpoint(responsesApi);
        using var http = new HttpClient(endpoint);
        var client = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://gateway.example.com/v1"),
                Transport = new HttpClientPipelineTransport(http),
            });
#pragma warning disable OPENAI001 // Responses adapter serialization is exercised against a fake endpoint.
        using var chat = responsesApi
            ? client.GetResponsesClient().AsIChatClient("gpt-6-luna")
            : client.GetChatClient("gpt-6-luna").AsIChatClient();
#pragma warning restore OPENAI001

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")], options);

        return JsonDocument.Parse(Assert.Single(endpoint.Requests));
    }

    private sealed class RecordingEndpoint(bool responsesApi) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            var body = responsesApi
                ? JsonSerializer.Serialize(
                    new
                    {
                        id = "resp_fake", @object = "response", created_at = 1770000000, model = "gpt-6-luna", status = "completed",
                        output = new[]
                        {
                            new
                            {
                                type = "message", id = "msg_fake", status = "completed", role = "assistant",
                                content = new[] { new { type = "output_text", text = "Done.", annotations = Array.Empty<object>() } },
                            },
                        },
                        parallel_tool_calls = false, tool_choice = "auto", tools = Array.Empty<object>(),
                        usage = new { input_tokens = 10, output_tokens = 2, total_tokens = 12 },
                    })
                : JsonSerializer.Serialize(
                    new
                    {
                        id = "chatcmpl_fake", @object = "chat.completion", created = 1770000000, model = "gpt-6-luna",
                        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "Done." } } },
                        usage = new { prompt_tokens = 10, completion_tokens = 2, total_tokens = 12 },
                    });

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
