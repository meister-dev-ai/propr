// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Covers what the Anthropic driver decides on the operator's behalf, and what it refuses outright.
/// </summary>
public sealed class AnthropicProviderDriverTests
{
    // Anthropic's own host is the common case, but the protocol is also served by gateways and enterprise
    // proxies. Pinning the host would refuse those for no reason the protocol requires.
    [Theory]
    [InlineData("https://api.anthropic.com/v1")]
    [InlineData("https://anthropic.gateway.example.com/v1")]
    public void AnEndpointSpeakingTheProtocolIsAcceptedWhereverItIsHosted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.XApiKey, HasApiKey: true)));
    }

    // Where the credential goes is the provider's rule, not a choice: a bearer token is a 401 here. An operator
    // who picked the ordinary API-key mode gets a working profile rather than a rejection to interpret.
    [Theory]
    [InlineData(AiAuthMode.ApiKey)]
    [InlineData(AiAuthMode.XApiKey)]
    public async Task TheCredentialIsSentTheWayAnthropicReadsItWhicheverModeWasConfigured(AiAuthMode authMode)
    {
        var wire = new RecordingEndpoint();
        var endpoint = new ProviderEndpoint(AiProviderKind.Anthropic, "https://api.anthropic.com/v1", authMode, "sk-ant-key");

        using var client = Driver(wire).CreateChatClient(endpoint, Model(), AiProtocolMode.Auto);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("sk-ant-key", wire.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Null(wire.Requests[0].Headers.Authorization);
    }

    [Fact]
    public void ADriverThatCannotEmbedSaysSoWithSomethingActionable()
    {
        var endpoint = new ProviderEndpoint(AiProviderKind.Anthropic, "https://api.anthropic.com/v1", AiAuthMode.XApiKey, "sk-ant-key");

        var failure = Assert.Throws<InvalidOperationException>(() => Driver().CreateEmbeddingGenerator(endpoint, Model(), AiProtocolMode.Auto, 1536));

        Assert.Contains("embedding", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 529 is Anthropic's own overload signal and sits outside the range a generic 5xx rule covers, so retrying
    // it has to be decided here or the most retryable failure the provider produces would be given up on.
    [Fact]
    public void ItsOwnOverloadSignalIsTreatedAsWorthRetrying()
    {
        var verdict = Driver().ClassifyRuntimeFailure(new HttpRequestException("overloaded", null, (HttpStatusCode)529));

        Assert.True(verdict.IsTransient);
    }

    [Fact]
    public void CachingIsClaimedBecauseTheNativeClientActuallyMarksABreakpoint()
    {
        var endpoint = new ProviderEndpoint(AiProviderKind.Anthropic, "https://api.anthropic.com/v1", AiAuthMode.XApiKey, "sk-ant-key");

        Assert.True(Driver().GetChatRuntimeCapabilities(endpoint, Model(), AiProtocolMode.Auto).SupportsPromptCaching);
    }

    private static ProviderModelDescriptor Model()
    {
        return new ProviderModelDescriptor(Guid.NewGuid(), "claude-opus-5", [AiProtocolMode.Auto, AiProtocolMode.AnthropicMessages]);
    }

    private static AnthropicProviderDriver Driver(HttpMessageHandler? runtimeHandler = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProviderAdmin");
        var runtime = services.AddHttpClient("AiProviderRuntime");
        if (runtimeHandler is not null)
        {
            runtime.ConfigurePrimaryHttpMessageHandler(() => runtimeHandler);
        }

        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        var provider = services.BuildServiceProvider();

        return new AnthropicProviderDriver(
            provider.GetRequiredService<OpenAiCompatibleTransport>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress: false,
            allowInsecureScheme: false);
    }

    private sealed class RecordingEndpoint : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":"msg_1","model":"claude-opus-5","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
        }
    }
}
