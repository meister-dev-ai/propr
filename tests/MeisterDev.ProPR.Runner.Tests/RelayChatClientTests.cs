// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The budget signal on the relay's answers. The control plane says the soft cap is reached on the
///     very completion that crossed it; a client that dropped that word had the review scan every
///     remaining file at full cost until the hard cap refused it mid-pass.
/// </summary>
public sealed class RelayChatClientTests
{
    private static readonly Guid JobId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public async Task ACompletionUnderTheCap_LeavesTheSignalUntouched()
    {
        var signal = new RunnerBudgetSignal();
        var client = Create(Respond(HttpStatusCode.OK, Envelope(softCapReached: false)), signal);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.False(signal.Exhausted);
    }

    [Fact]
    public async Task TheCompletionThatCrossesTheSoftCap_LatchesTheSignal()
    {
        var signal = new RunnerBudgetSignal();
        var client = Create(Respond(HttpStatusCode.OK, Envelope(softCapReached: true)), signal);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        Assert.True(signal.Exhausted);
    }

    // The hard stop doubles as the wind-down: every file not yet started would meet the same refusal,
    // so the planner should stop starting them rather than fail them one refusal at a time.
    [Fact]
    public async Task ARefusedCompletion_LatchesTheSignalAndStillThrows()
    {
        var signal = new RunnerBudgetSignal();
        var client = Create(Respond(HttpStatusCode.PaymentRequired, """{"code":"budget_cap_reached","message":"spent"}"""), signal);

        await Assert.ThrowsAsync<RelayRefusedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]));

        Assert.True(signal.Exhausted);
    }

    // The options the pipeline shaped are what make a review a review: without the tools on the wire, a
    // remote review runs zero tool calls and ends after one turn, which is exactly what the first live
    // runs did.
    [Fact]
    public async Task TheOptionsThePipelineBuilt_RideTheWire()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, Envelope(softCapReached: false));
        var client = Create(handler, new RunnerBudgetSignal());
        var options = new ChatOptions
        {
            Temperature = 0.2f,
            MaxOutputTokens = 9000,
            Tools = [AIFunctionFactory.Create((string path) => path, "get_file_content", "Reads a file at head.")],
            RawRepresentationFactory = chatClient => chatClient is INativeProtocolChatClient
                ? new ProviderReasoningRequest(ProviderReasoningEffort.High, CaptureReasoning: true)
                : null,
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")], options);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var sent = body.RootElement.GetProperty("options");
        Assert.Equal(0.2f, sent.GetProperty("temperature").GetSingle());
        Assert.Equal(9000, sent.GetProperty("maxOutputTokens").GetInt32());
        var tool = Assert.Single(sent.GetProperty("tools").EnumerateArray().ToList());
        Assert.Equal("get_file_content", tool.GetProperty("name").GetString());
        Assert.Equal("Reads a file at head.", tool.GetProperty("description").GetString());
        Assert.True(tool.GetProperty("schema").GetProperty("properties").TryGetProperty("path", out _));
        Assert.Equal("high", sent.GetProperty("reasoningEffort").GetString());
        Assert.True(sent.GetProperty("captureReasoning").GetBoolean());
    }

    [Fact]
    public async Task ACallShapedByNothing_SendsNoOptions()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, Envelope(softCapReached: false));
        var client = Create(handler, new RunnerBudgetSignal());

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "review this")]);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("options").ValueKind);
    }

    private static RelayChatClient Create(HttpMessageHandler handler, RunnerBudgetSignal signal)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/runners/execution/") };
        return new RelayChatClient(http, JobId, 4, "reviewer-default", signal);
    }

    private static string Envelope(bool softCapReached)
    {
        return
            $$"""{"response":{"messages":[{"role":"assistant","contents":[{"$type":"text","text":"ok"}]}]},"softCapReached":{{(softCapReached ? "true" : "false")}},"replayed":false}""";
    }

    private static StubHandler Respond(HttpStatusCode status, string body)
    {
        return new StubHandler(status, body);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
