// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.LiteLlmAddIn.Tests;

/// <summary>
///     This family's own rules. The load-bearing one is the address: a gateway is usually on a private address
///     inside the operator's own network, so what the installation permits an address to reach decides whether a
///     connection can be configured at all.
/// </summary>
public sealed class LiteLlmProviderDriverTests
{
    // A wire shape another family declares, which a binding holds after a connection is repointed and
    // is a shape this family cannot serve.
    private const string AnotherFamilysProtocol = "meisterdev/anthropic:AnthropicMessages";

    private const string Gateway = "https://gateway.example.com/v1";
    private const string PrivateGateway = "https://10.0.0.5:4000/v1";

    [Fact]
    public void APublicHttpsGatewayWithAKeyIsAccepted()
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(Gateway, LiteLlmProviderDriver.ApiKeyAuth, true)));
    }

    // A gateway runs wherever the operator put it, and an Azure resource behind one is the gateway's business.
    [Theory]
    [InlineData("https://x.openai.azure.com/")]
    [InlineData("https://llm.contoso.example/v1")]
    public void AGatewayIsTakenWhoeverRunsIt(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, LiteLlmProviderDriver.ApiKeyAuth, true)));
    }

    // The identity key is what every connection of this family is stored against, so it is pinned:
    // changing it silently reinterprets every row that carries it.
    [Fact]
    public void TheDriverDeclaresTheIdentityItsConnectionsAreStoredUnder()
    {
        Assert.Equal("meisterdev/liteLlm", Driver().Declaration.Key);
    }

    // The case this family exists for, in both directions. The installation's opt-in reaches a self-hosted
    // gateway; without it, the same address is refused.
    [Theory]
    [InlineData(PrivateGateway)]
    [InlineData("https://192.168.1.10:4000/v1")]
    [InlineData("https://127.0.0.1:4000/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void AGatewayOnAPrivateAddressIsRefusedWithoutTheInstallationsOptIn(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, LiteLlmProviderDriver.ApiKeyAuth, true));

        Assert.Contains("private", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PrivateGateway)]
    [InlineData("https://192.168.1.10:4000/v1")]
    [InlineData("https://127.0.0.1:4000/v1")]
    public void AGatewayOnAPrivateAddressIsTakenWithTheInstallationsOptIn(string baseUrl)
    {
        var target = new AiProbeTarget(baseUrl, LiteLlmProviderDriver.ApiKeyAuth, true) { AllowsPrivateAddress = true };

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    // The opt-in reaches a private address and does not relax transport security: an on-premise gateway is still
    // reached over https.
    [Fact]
    public void PermittingAPrivateAddressDoesNotPermitPlainHttp()
    {
        var target = new AiProbeTarget("http://10.0.0.5:4000/v1", LiteLlmProviderDriver.ApiKeyAuth, true) { AllowsPrivateAddress = true };

        Assert.Contains("https", Driver().ValidateProbeTarget(target), StringComparison.Ordinal);
    }

    // A Development host relaxes the scheme as well, which is how a gateway is reached on a developer's machine.
    [Fact]
    public void PlainHttpIsTakenWhereTheInstallationPermitsIt()
    {
        var target = new AiProbeTarget("http://localhost:4000/v1", LiteLlmProviderDriver.ApiKeyAuth, true)
        {
            AllowsPrivateAddress = true,
            AllowsInsecureScheme = true,
        };

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    // This check reads the address as written, so it turns away an IP literal and cannot know where a name
    // resolves. A name that resolves inward is stopped at connect time instead, by the check the host composes
    // innermost in every client it hands out.
    [Theory]
    [InlineData("https://localhost:4000/v1")]
    [InlineData("https://litellm.internal/v1")]
    public void APrivateHostnameIsLeftToTheConnectTimeCheck(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, LiteLlmProviderDriver.ApiKeyAuth, true)));
    }

    [Fact]
    public void AProfileWithNoVirtualKeyIsRefusedWhileTheOperatorIsStillConfiguringIt()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(Gateway, LiteLlmProviderDriver.ApiKeyAuth, false));

        Assert.Contains("API key", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialInTheAddressIsRefused()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("https://user:secret@gateway.example.com/v1", LiteLlmProviderDriver.ApiKeyAuth, true));

        Assert.Contains("credential", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheModelListIsReadFromTheGatewaysOwnPath()
    {
        var wire = new FakeGateway(HttpStatusCode.OK, """{"data":[{"id":"claude-opus-4-5"},{"id":"gpt-5"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("succeeded", discovered.DiscoveryStatus);
        Assert.Equal(["claude-opus-4-5", "gpt-5"], discovered.Models.Select(model => model.RemoteModelId));

        var request = Assert.Single(wire.Requests);
        Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("virtual-key", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task AGatewayOnAPrivateAddressIsReachedThroughTheHostsTransport()
    {
        var wire = new FakeGateway(HttpStatusCode.OK, """{"data":[{"id":"gpt-5"}]}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire, PrivateGateway));

        Assert.Equal(AiVerificationStatus.Verified, verification.Status);
        Assert.Equal(new Uri($"{PrivateGateway}/models"), Assert.Single(wire.Requests).RequestUri);
    }

    // A model list longer than the host reads cannot be parsed, and presenting the part that was read as the
    // gateway's own list would be wrong. Both callers of the listing read a refusal and neither catches an
    // exception, so it is reported as one rather than raised.
    [Fact]
    public async Task AModelListLongerThanTheHostReadsIsAFailedDiscovery()
    {
        var wire = new FakeGateway(
            HttpStatusCode.OK,
            $$"""{"data":[{"id":"{{new string('m', 5 * 1024 * 1024)}}"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("failed", discovered.DiscoveryStatus);
        Assert.Empty(discovered.Models);
    }

    [Fact]
    public async Task AModelListLongerThanTheHostReadsIsAFailedVerification()
    {
        var wire = new FakeGateway(
            HttpStatusCode.OK,
            $$"""{"data":[{"id":"{{new string('m', 5 * 1024 * 1024)}}"}]}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
    }

    [Fact]
    public async Task AGatewayThatRefusesTheVirtualKeyReportsWhatItSaid()
    {
        var wire = new FakeGateway(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid virtual key"}}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
        Assert.Contains("invalid virtual key", verification.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AWireProtocolThisFamilyDoesNotSpeakIsRefusedByName()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Driver().CreateChatClient(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [ProviderDeclaredProtocolModes.Auto]),
            AnotherFamilysProtocol));

        Assert.Contains(AnotherFamilysProtocol, failure.Message, StringComparison.Ordinal);
    }

    // A host builds a client to describe this family as well as to call it, so building one without a transport
    // has to succeed. Calling one does not: a client that reached the network on its own would not carry the
    // host's connect-time address check, which admits a private address only where the installation
    // permits one.
    [Fact]
    public async Task ACallWithNoHostTransportRefusesInsteadOfReachingTheNetwork()
    {
        using var chat = Driver().CreateChatClient(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [LiteLlmProviderDriver.ChatCompletionsProtocol]),
            LiteLlmProviderDriver.ChatCompletionsProtocol);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("client factory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelCallGoesOutOnTheHostsTransport()
    {
        var wire = new FakeGateway(
            HttpStatusCode.OK,
            """{"id":"1","object":"chat.completion","created":0,"model":"gpt-5","choices":[]}""");

        using var chat = Driver().CreateChatClient(
            EndpointOn(wire, PrivateGateway),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [LiteLlmProviderDriver.ChatCompletionsProtocol]),
            LiteLlmProviderDriver.ChatCompletionsProtocol);

        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(new Uri($"{PrivateGateway}/chat/completions"), Assert.Single(wire.Requests).RequestUri);
    }

    // The gateway forwards to whatever is behind it, so it serves the wire shapes that vendor serves.
    [Fact]
    public void TheResponsesShapeBuildsAClientRatherThanBeingRefused()
    {
        using var wire = new FakeGateway(HttpStatusCode.OK, "{}");

        using var chat = Driver().CreateChatClient(
            EndpointOn(wire),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [LiteLlmProviderDriver.ResponsesProtocol]),
            LiteLlmProviderDriver.ResponsesProtocol);

        Assert.NotNull(chat);
    }

    // A provider-managed session and a background response are held on the connection that opened them, and the
    // gateway states neither, so claiming one would leave a review waiting for a continuation that never
    // arrives. Asserted for the wire shape most likely to tempt a claim.
    [Fact]
    public void NoProviderManagedAffordanceIsClaimed()
    {
        var capabilities = Driver().GetChatRuntimeCapabilities(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [LiteLlmProviderDriver.ResponsesProtocol]),
            LiteLlmProviderDriver.ResponsesProtocol);

        Assert.Equal(ProviderRuntimeCapabilities.None, capabilities);
    }

    // The transport rides on the endpoint, because the driver is constructed once for the family and every
    // connection of it is reached through the client factory the host attaches to that connection.
    private static ProviderEndpoint EndpointOn(HttpMessageHandler? wire, string baseUrl = Gateway)
    {
        return new ProviderEndpoint("meisterdev/liteLlm", baseUrl, LiteLlmProviderDriver.ApiKeyAuth, "virtual-key")
        {
            HostContext = wire is null ? null : new FakeProviderHostContext(wire),
        };
    }

    private static LiteLlmProviderDriver Driver()
    {
        return new LiteLlmProviderDriver();
    }

    /// <summary>Answers every request the same way and keeps what was sent.</summary>
    private sealed class FakeGateway(HttpStatusCode status, string body) : HttpMessageHandler
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
