// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.OpenAiCompatibleAddIn.Tests;

/// <summary>
///     This family's own rules: which endpoints it takes, what it makes of a model list, and what it does when
///     the host hands it no way out to the network.
/// </summary>
public sealed class OpenAiCompatibleProviderDriverTests
{
    // The Responses shape under this family's own key. It is deliberately not declared: the surface is
    // OpenAI-specific and assuming it of a compatible endpoint turns into a 404 on the first call.
    private const string UndeclaredResponsesProtocol = OpenAiCompatibleProviderDriver.FamilyKey + ":Responses";

    private const string Endpoint = "https://opencode.ai/zen/v1";

    [Fact]
    public void APublicHttpsEndpointWithAKeyIsAccepted()
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(Endpoint, OpenAiCompatibleProviderDriver.ApiKeyAuth, true)));
    }

    // The whole point of the family: an endpoint belonging to no vendor this product knows is what it serves,
    // and an Azure resource fronted by a compatible gateway is one of them.
    [Theory]
    [InlineData("https://x.openai.azure.com/")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://gateway.contoso.example/v1")]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://api.groq.com/openai/v1")]
    public void AnEndpointIsTakenWhoeverRunsIt(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, OpenAiCompatibleProviderDriver.ApiKeyAuth, true)));
    }

    // The identity key is what every connection of this family is stored against, so it is pinned:
    // changing it silently reinterprets every row that carries it.
    [Fact]
    public void TheDriverDeclaresTheIdentityItsConnectionsAreStoredUnder()
    {
        Assert.Equal("meisterdev/openAiCompatible", Driver().Declaration.Key);
    }

    // The families this product ships beside this one, named here because this add-in references none of them
    // and a refusal has to name none of them either.
    private static readonly string[] OtherShippedFamilies =
        ["AzureOpenAi", "OpenAi", "LiteLlm", "Anthropic", "AwsBedrock", "GoogleVertex"];

    // A refusal reaches the operator configuring the connection, so it names what they have to change and
    // nothing about a provider family they did not pick.
    [Fact]
    public void NoRefusalNamesAnotherProviderFamily()
    {
        var driver = Driver();
        string?[] refusals =
        [
            driver.ValidateProbeTarget(new AiProbeTarget("not-a-url", OpenAiCompatibleProviderDriver.ApiKeyAuth, true)),
            driver.ValidateProbeTarget(new AiProbeTarget("http://api.example.com/v1", OpenAiCompatibleProviderDriver.ApiKeyAuth, true)),
            driver.ValidateProbeTarget(new AiProbeTarget("https://10.0.0.5/v1", OpenAiCompatibleProviderDriver.ApiKeyAuth, true)),
            driver.ValidateProbeTarget(new AiProbeTarget(Endpoint, OpenAiCompatibleProviderDriver.ApiKeyAuth, false)),
        ];

        Assert.All(
            refusals,
            refusal => Assert.All(
                OtherShippedFamilies,
                family => Assert.DoesNotContain(family, refusal ?? string.Empty, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PlainHttpIsRefusedWhereTheInstallationDoesNotPermitIt()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("http://api.example.com/v1", OpenAiCompatibleProviderDriver.ApiKeyAuth, true));

        Assert.Contains("https", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://10.0.0.5/v1")]
    [InlineData("https://127.0.0.1:8080/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://192.168.1.10:11434/v1")]
    [InlineData("https://[::1]:8000/v1")]
    public void AnAddressOnTheHostsOwnNetworkIsRefusedWhereTheInstallationDoesNotPermitIt(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, OpenAiCompatibleProviderDriver.ApiKeyAuth, true));

        Assert.Contains("private", refusal, StringComparison.Ordinal);
    }

    // This check reads the address as written, so it turns away an IP literal and cannot know where a name
    // resolves. A name that resolves inward is stopped at connect time instead, by the check the host composes
    // innermost in every client it hands out, and the operator still learns at probe time because the probe
    // leaves through that client.
    [Theory]
    [InlineData("https://localhost:11434/v1")]
    [InlineData("https://ollama.internal/v1")]
    public void APrivateHostnameIsLeftToTheConnectTimeCheck(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, OpenAiCompatibleProviderDriver.ApiKeyAuth, true)));
    }

    // The installation's opt-in reaches a self-hosted endpoint. The family is told what the installation permits
    // on the target, because it is constructed with no arguments and has no other way to learn it.
    [Fact]
    public void AnAddressOnTheHostsOwnNetworkIsTakenWhereTheInstallationPermitsIt()
    {
        var target = new AiProbeTarget("https://10.0.0.5/v1", OpenAiCompatibleProviderDriver.ApiKeyAuth, true) { AllowsPrivateAddress = true };

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    // The opt-in reaches a private address and does not relax transport security.
    [Fact]
    public void PermittingAPrivateAddressDoesNotPermitPlainHttp()
    {
        var target = new AiProbeTarget("http://10.0.0.5/v1", OpenAiCompatibleProviderDriver.ApiKeyAuth, true) { AllowsPrivateAddress = true };

        Assert.Contains("https", Driver().ValidateProbeTarget(target), StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileWithNoCredentialIsRefusedWhileTheOperatorIsStillConfiguringIt()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(Endpoint, OpenAiCompatibleProviderDriver.ApiKeyAuth, false));

        Assert.Contains("API key", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialInTheAddressIsRefused()
    {
        var refusal = Driver()
            .ValidateProbeTarget(new AiProbeTarget("https://user:secret@api.example.com/v1", OpenAiCompatibleProviderDriver.ApiKeyAuth, true));

        Assert.Contains("credential", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelListIsReadFromTheEndpointsOwnPath()
    {
        var wire = new FakeCompatibleEndpoint(HttpStatusCode.OK, """{"data":[{"id":"kimi-k2"},{"id":"gpt-oss-120b"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("succeeded", discovered.DiscoveryStatus);
        Assert.Equal(["gpt-oss-120b", "kimi-k2"], discovered.Models.Select(model => model.RemoteModelId));

        var request = Assert.Single(wire.Requests);
        Assert.Equal("/zen/v1/models", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("zen-key", request.Headers.Authorization.Parameter);
    }

    // A compatible endpoint publishes identifiers and no capabilities, so what a model is offered as is read off
    // its identifier and corrected by the operator.
    [Fact]
    public async Task AModelNamedAsAnEmbeddingIsOfferedAsOne()
    {
        var wire = new FakeCompatibleEndpoint(HttpStatusCode.OK, """{"data":[{"id":"text-embedding-3-small"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        var model = Assert.Single(discovered.Models);
        Assert.Equal([AiOperationKind.Embedding], model.OperationKinds);
        Assert.Equal([ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings], model.SupportedProtocolModes);
    }

    // The Responses API is an affordance of one vendor's own surface. Offering it here turns into a 404 on the
    // first call against a server that implements chat completions and nothing else.
    [Fact]
    public async Task ADiscoveredChatModelIsNotOfferedTheResponsesApi()
    {
        var wire = new FakeCompatibleEndpoint(HttpStatusCode.OK, """{"data":[{"id":"kimi-k2"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.DoesNotContain(UndeclaredResponsesProtocol, Assert.Single(discovered.Models).SupportedProtocolModes);
    }

    [Fact]
    public async Task AnEndpointThatAnswersTheModelListIsVerified()
    {
        var wire = new FakeCompatibleEndpoint(HttpStatusCode.OK, """{"data":[{"id":"kimi-k2"}]}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Verified, verification.Status);
    }

    [Fact]
    public async Task AnEndpointThatRefusesTheCredentialReportsWhatItSaid()
    {
        var wire = new FakeCompatibleEndpoint(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
        Assert.Contains("bad key", verification.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AWireProtocolThisFamilyDoesNotSpeakIsRefusedByName()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Driver().CreateChatClient(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "kimi-k2", [ProviderDeclaredProtocolModes.Auto]),
            UndeclaredResponsesProtocol));

        Assert.Contains(UndeclaredResponsesProtocol, failure.Message, StringComparison.Ordinal);
    }

    // A host builds a client to describe this family as well as to call it, so building one without a transport
    // has to succeed. Calling one does not: a client that reached the network on its own would not carry the
    // host's connect-time address check.
    [Fact]
    public async Task ACallWithNoHostTransportRefusesInsteadOfReachingTheNetwork()
    {
        using var chat = Driver().CreateChatClient(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "kimi-k2", [OpenAiCompatibleProviderDriver.ChatCompletionsProtocol]),
            OpenAiCompatibleProviderDriver.ChatCompletionsProtocol);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("client factory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelCallGoesOutOnTheHostsTransport()
    {
        var wire = new FakeCompatibleEndpoint(
            HttpStatusCode.OK,
            """{"id":"1","object":"chat.completion","created":0,"model":"kimi-k2","choices":[]}""");

        using var chat = Driver().CreateChatClient(
            EndpointOn(wire),
            new ProviderModelDescriptor(Guid.NewGuid(), "kimi-k2", [OpenAiCompatibleProviderDriver.ChatCompletionsProtocol]),
            OpenAiCompatibleProviderDriver.ChatCompletionsProtocol);

        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var request = Assert.Single(wire.Requests);
        Assert.Equal("/zen/v1/chat/completions", request.RequestUri!.AbsolutePath);
    }

    // The transport rides on the endpoint, because the driver is constructed once for the family and every
    // connection of it is reached through the client factory the host attaches to that connection.
    private static ProviderEndpoint EndpointOn(HttpMessageHandler? wire)
    {
        return new ProviderEndpoint("meisterdev/openAiCompatible", Endpoint, OpenAiCompatibleProviderDriver.ApiKeyAuth, "zen-key")
        {
            HostContext = wire is null ? null : new FakeProviderHostContext(wire),
        };
    }

    private static OpenAiCompatibleProviderDriver Driver()
    {
        return new OpenAiCompatibleProviderDriver();
    }

    /// <summary>Answers every request the same way and keeps what was sent.</summary>
    private sealed class FakeCompatibleEndpoint(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);

            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}
