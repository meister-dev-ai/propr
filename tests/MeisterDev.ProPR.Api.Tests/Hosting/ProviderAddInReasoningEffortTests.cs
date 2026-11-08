// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// The Responses options of the OpenAI library are marked for evaluation only.
#pragma warning disable OPENAI001

namespace MeisterDev.ProPR.Api.Tests.Hosting;

/// <summary>
///     A request with a configured reasoning effort, sent through the bundled OpenAI add-in loaded in its own load
///     context, as the host loads every provider family. The add-in carries its own copy of the OpenAI library, so the
///     OpenAI options the host builds are a foreign type for the add-in.
/// </summary>
public sealed class ProviderAddInReasoningEffortTests
{
    private const string ResponsesProtocol = "meisterdev/openAi:Responses";

    private const string ResponseBody =
        """
        {
          "id": "resp_fake", "object": "response", "created_at": 1770000000, "model": "m", "status": "completed",
          "output": [{
            "type": "message", "id": "msg_fake", "status": "completed", "role": "assistant",
            "content": [{ "type": "output_text", "text": "hi", "annotations": [] }]
          }],
          "parallel_tool_calls": false, "tool_choice": "auto", "tools": []
        }
        """;

    // For a model called through the Responses API, a relayed effort reaches the request of the add-in.
    [Fact]
    public async Task ARelayedEffort_ReachesTheResponsesApiThroughTheBundledOpenAiAddIn()
    {
        var options = RunnerRelayedChatOptions.ToChatOptions(
            new RunnerChatOptions(ReasoningEffort: "high"),
            captureReasoning: false,
            usesResponsesApi: true);

        var sentEffort = await SendThroughTheBundledOpenAiAddInAsync(options);

        Assert.Equal("high", sentEffort);
    }

    // OpenAI options that the host builds are a foreign type for the add-in, which loads its own copy of the OpenAI
    // library, so they do not carry the effort into the add-in. The add-in reads the neutral request instead.
    [Fact]
    public async Task TheOpenAiOptionsOfTheHostAlone_DoNotCarryTheEffortThroughTheBundledOpenAiAddIn()
    {
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.High },
            },
        };

        var sentEffort = await SendThroughTheBundledOpenAiAddInAsync(options);

        Assert.Null(sentEffort);
    }

    // The add-in reads the level by its name, so a level that the shared reasoning setting has no value for, such as
    // max, reaches the Responses API.
    [Fact]
    public async Task ALevelByName_ReachesTheResponsesApiThroughTheBundledOpenAiAddIn()
    {
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => new ProviderReasoningRequest(ProviderReasoningEffort.High, false) { Level = "max" },
        };

        var sentEffort = await SendThroughTheBundledOpenAiAddInAsync(options);

        Assert.Equal("max", sentEffort);
    }

    // Loads the bundled OpenAI add-in from the directory the build copies it to, in a load context of its own, and
    // returns the reasoning effort of the request the add-in sent, or null when the request carried none.
    private static async Task<string?> SendThroughTheBundledOpenAiAddInAsync(ChatOptions? options)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "provider-add-ins",
            "MeisterDev.Ai.Providers.OpenAiAddIn",
            "MeisterDev.Ai.Providers.OpenAiAddIn.dll");
        var assembly = new ProviderAddInLoadContext(path).LoadFromAssemblyPath(path);
        var driverType = assembly.GetTypes()
            .Single(type => typeof(IAiProviderDriver).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });
        var driver = (IAiProviderDriver)Activator.CreateInstance(driverType)!;

        using var wire = new RecordingEndpoint();
        var endpoint = new ProviderEndpoint("meisterdev/openAi", "https://api.openai.example/v1", "meisterdev/openAi:ApiKey", "sk-test")
        {
            HostContext = new TransportOnlyHostContext(wire),
        };
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "gpt-6.1-terra", [ResponsesProtocol]);
        using var chat = driver.CreateChatClient(endpoint, model, ResponsesProtocol);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")], options);

        using var request = JsonDocument.Parse(Assert.Single(wire.Bodies));
        return request.RootElement.TryGetProperty("reasoning", out var reasoning)
               && reasoning.ValueKind == JsonValueKind.Object
               && reasoning.TryGetProperty("effort", out var effort)
            ? effort.GetString()
            : null;
    }

    /// <summary>Supplies the transport and nothing else, which is all the OpenAI family asks of a host.</summary>
    /// <param name="wire">What the clients this context hands out send to.</param>
    private sealed class TransportOnlyHostContext(HttpMessageHandler wire) : IProviderConnectionContext
    {
        public IProviderHttpClientFactory Http { get; } = new Factory(wire);

        public IProviderCredentialSessions Credentials =>
            throw new NotSupportedException("The OpenAI family reads its credential from the endpoint.");

        public IProviderLeases Leases => throw new NotSupportedException("The OpenAI family arbitrates no resource.");

        public IProviderKeyedStore Store => throw new NotSupportedException("The OpenAI family keeps no state between calls.");

        public IProviderHealthSignal Health => throw new NotSupportedException("The OpenAI family reports no credential health.");

        private sealed class Factory(HttpMessageHandler wire) : IProviderHttpClientFactory
        {
            public HttpClient Create(ProviderHttpPurpose purpose, IReadOnlyList<DelegatingHandler>? outerHandlers = null)
            {
                var pipeline = wire;
                for (var index = outerHandlers?.Count - 1; index >= 0; index--)
                {
                    var handler = outerHandlers![index.Value];
                    handler.InnerHandler = pipeline;
                    pipeline = handler;
                }

                return new HttpClient(pipeline, disposeHandler: false);
            }
        }
    }

    /// <summary>Records each request body and answers with a completed response.</summary>
    private sealed class RecordingEndpoint : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
