// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenAI;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    [Theory]
    [InlineData(false, "gpt-4o")]
    [InlineData(true, "gpt-4o")]
    [InlineData(true, "gpt-5-pro")]
    [InlineData(false, "gateway-alias")]
    [InlineData(true, "gateway-alias")]
    public async Task ModelsWithoutPositiveLowEffortSupport_KeepProviderDefaultsAndPublish(bool responsesApi, string modelId)
    {
        using var wire = new JudgeWireEndpoint(responsesApi, false, rejectsReasoning: true);
        using var http = new HttpClient(wire);
        using var chat = BuildWireChatClient(http, responsesApi, modelId);
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var resolver = Substitute.For<IAiRuntimeResolver>();
        var runtime = RuntimeFor(chat, modelId);
        var configuredModel = runtime.Model with { SupportsReasoning = true };
        runtime.Model.Returns(configuredModel);
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), MeisterDev.ProPR.Domain.Enums.AiPurpose.ReviewVerification, Arg.Any<CancellationToken>())
            .Returns(runtime);

        var outcomes = await new EvidenceBackedReviewVerifier().VerifyAsync(
            [CreateWorkItem("src/Lookup.cs", 1)], [],
            new ReviewVerificationContext(tools, "source", chat, modelId, Guid.NewGuid(), resolver));

        Assert.Equal(FinalGateDecision.PublishDisposition, Assert.Single(outcomes).RecommendedDisposition);
        using var request = JsonDocument.Parse(Assert.Single(wire.Requests));
        Assert.False(request.RootElement.TryGetProperty(responsesApi ? "reasoning" : "reasoning_effort", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClientWithoutResolvedCapabilities_KeepsProviderReasoningDefaults(bool responsesApi)
    {
        using var wire = new JudgeWireEndpoint(responsesApi, false);
        using var http = new HttpClient(wire);
        using var chat = BuildWireChatClient(http, responsesApi);
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var outcomes = await new EvidenceBackedReviewVerifier().VerifyAsync(
            [CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", chat, "gpt-5"));

        Assert.Equal(FinalGateDecision.PublishDisposition, Assert.Single(outcomes).RecommendedDisposition);
        using var request = JsonDocument.Parse(Assert.Single(wire.Requests));
        Assert.False(request.RootElement.TryGetProperty(responsesApi ? "reasoning" : "reasoning_effort", out _));
    }

    // The judge asks a model with low effort support for low effort. The Responses API receives it, and Chat
    // Completions receives no effort, as for a review pass.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealOpenAiAdapters_SendLowJudgeEffortToResponsesOnlyAndRecoverTheWireOutputLimit(bool responsesApi)
    {
        using var wire = new JudgeWireEndpoint(responsesApi, true);
        using var http = new HttpClient(wire);
        using var chat = BuildWireChatClient(http, responsesApi);
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        var runtime = RuntimeFor(chat, "gpt-5");
        runtime.SupportsLowReasoningEffort.Returns(true);
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), MeisterDev.ProPR.Domain.Enums.AiPurpose.ReviewVerification, Arg.Any<CancellationToken>())
            .Returns(runtime);

        var outcomes = await new EvidenceBackedReviewVerifier().VerifyAsync(
            [CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", chat, "gpt-5", Guid.NewGuid(), resolver));

        Assert.Equal(FinalGateDecision.PublishDisposition, Assert.Single(outcomes).RecommendedDisposition);
        Assert.Equal(2, wire.Requests.Count);
        AssertWireJudgeOptions(wire.Requests[0], responsesApi, 4000);
        AssertWireJudgeOptions(wire.Requests[1], responsesApi, 8000);
        using var retry = JsonDocument.Parse(wire.Requests[1]);
        Assert.Equal("none", retry.RootElement.GetProperty("tool_choice").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RelayedJudgeOptions_SendLowEffortToTheResponsesApiOnly(bool responsesApi, bool resolvedPolicy)
    {
        using var wire = new JudgeWireEndpoint(responsesApi, false);
        using var http = new HttpClient(wire);
        using var chat = BuildWireChatClient(http, responsesApi);
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(MaxOutputTokens: 8000, ReasoningEffort: "low"),
            captureReasoning: resolvedPolicy ? false : null);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Verify the finding.")], options);

        AssertWireJudgeOptions(Assert.Single(wire.Requests), responsesApi, 8000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelayedDisabledToolSelection_ReachesBothOpenAiAdapters(bool responsesApi)
    {
        using var wire = new JudgeWireEndpoint(responsesApi, false);
        using var http = new HttpClient(wire);
        using var chat = BuildWireChatClient(http, responsesApi);
        var relayed = JsonSerializer.Deserialize<RunnerChatOptions>(
            """{"toolMode":"none","tools":[{"name":"get_file_content","schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Return the verdict.")], RunnerRelayedChatOptions.ToChatOptions(relayed));

        using var request = JsonDocument.Parse(Assert.Single(wire.Requests));
        Assert.Equal("none", request.RootElement.GetProperty("tool_choice").GetString());
        Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
    }

    private static IChatClient BuildWireChatClient(HttpClient http, bool responsesApi, string modelId = "gpt-5")
    {
        var client = new OpenAIClient(
            new ApiKeyCredential("test-key"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://gateway.example.com/v1"),
                Transport = new HttpClientPipelineTransport(http),
            });
#pragma warning disable OPENAI001 // Responses adapter serialization is exercised against a fake endpoint.
        return responsesApi ? client.GetResponsesClient().AsIChatClient(modelId) : client.GetChatClient(modelId).AsIChatClient();
#pragma warning restore OPENAI001
    }

    // The Responses API receives the low judge effort. Chat Completions receives no low effort, because OpenAI refuses
    // function tools there with any effort other than none from GPT-5.4 on.
    private static void AssertWireJudgeOptions(string body, bool responsesApi, int outputBudget)
    {
        using var request = JsonDocument.Parse(body);
        var root = request.RootElement;
        if (responsesApi)
        {
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(root.GetProperty("reasoning").TryGetProperty("summary", out _));
            Assert.Equal(outputBudget, root.GetProperty("max_output_tokens").GetInt32());
        }
        else
        {
            Assert.False(root.TryGetProperty("reasoning_effort", out _));
            var budget = root.TryGetProperty("max_completion_tokens", out var completionTokens) ? completionTokens : root.GetProperty("max_tokens");
            Assert.Equal(outputBudget, budget.GetInt32());
        }
    }

    private sealed class JudgeWireEndpoint(bool responsesApi, bool exhaustFirstResponse, bool rejectsReasoning = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            using var payload = JsonDocument.Parse(this.Requests[^1]);
            if (rejectsReasoning && payload.RootElement.TryGetProperty(responsesApi ? "reasoning" : "reasoning_effort", out _))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Unsupported reasoning parameter for this model","type":"invalid_request_error","param":"reasoning_effort","code":"unsupported_parameter"}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            var exhausted = exhaustFirstResponse && this.Requests.Count == 1;
            var outputTokens = exhausted ? 4000 : 20;
            var reasoningTokens = exhausted ? 4000 : 0;
            var text = exhausted ? string.Empty : ConfirmedBudgetVerdict;
            var body = responsesApi
                ? JsonSerializer.Serialize(
                    new
                    {
                        id = "resp_fake", @object = "response", created_at = 1770000000, model = "gpt-5",
                        status = exhausted ? "incomplete" : "completed",
                        incomplete_details = exhausted ? new { reason = "max_output_tokens" } : null,
                        output = new[]
                        {
                            new
                            {
                                type = "message", id = "msg_fake", status = "completed", role = "assistant",
                                content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } },
                            },
                        },
                        parallel_tool_calls = false, tool_choice = "auto", tools = Array.Empty<object>(),
                        usage = new
                        {
                            input_tokens = 100, output_tokens = outputTokens, total_tokens = 100 + outputTokens,
                            output_tokens_details = new { reasoning_tokens = reasoningTokens },
                        },
                    })
                : JsonSerializer.Serialize(
                    new
                    {
                        id = "chatcmpl_fake", @object = "chat.completion", created = 1770000000, model = "gpt-5",
                        choices = new[]
                        {
                            new { index = 0, finish_reason = exhausted ? "length" : "stop", message = new { role = "assistant", content = text } },
                        },
                        usage = new
                        {
                            prompt_tokens = 100, completion_tokens = outputTokens, total_tokens = 100 + outputTokens,
                            completion_tokens_details = new { reasoning_tokens = reasoningTokens },
                        },
                    });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
