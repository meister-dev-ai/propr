// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.AnthropicAddIn.Tests;

/// <summary>
///     This family's own rules: which endpoints it takes, what it decides on the operator's behalf, what it
///     refuses outright, and what it does when the host hands it no way out to the network.
/// </summary>
public sealed class AnthropicProviderDriverTests
{
    // A credential shape another family declares, which a connection holds after it is repointed and is
    // material this family cannot read.
    private const string AnotherFamilysCredential = "meisterdev/azureOpenAi:AzureIdentity";

    // A wire shape another family declares, for the same reason.
    private const string AnotherFamilysProtocol = "meisterdev/openAi:ChatCompletions";

    private const string VendorEndpoint = "https://api.anthropic.com/v1";

    // Anthropic's own host is the common case, but the protocol is also served by gateways and enterprise
    // proxies. Pinning the host would refuse those for no reason the protocol requires.
    [Theory]
    [InlineData("https://api.anthropic.com/v1")]
    [InlineData("https://anthropic.gateway.example.com/v1")]
    public void AnEndpointSpeakingTheProtocolIsAcceptedWhereverItIsHosted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AnthropicProviderDriver.ApiKeyAuth, HasApiKey: true)));
    }

    // The family declares one credential shape, because one API key is all an operator supplies. Where that key
    // is written on the wire is this family's business and not a second shape to choose between.
    [Fact]
    public void TheDeclaredCredentialShapeIsAccepted()
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(VendorEndpoint, AnthropicProviderDriver.ApiKeyAuth, HasApiKey: true)));
    }

    // Reachable only for a stored profile carrying another family's shape, since the console offers the one this
    // family declares. It names that shape, because the header the key travels in is not something an operator
    // picks and telling them about it would not say what to do.
    [Fact]
    public void ACredentialShapeAnthropicCannotReadIsRefusedAndNamesTheOneItReads()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(VendorEndpoint, AnotherFamilysCredential, HasApiKey: true));

        Assert.Contains("API Key", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileWithNoCredentialIsRefusedWhileTheOperatorIsStillConfiguringIt()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(VendorEndpoint, AnthropicProviderDriver.ApiKeyAuth, HasApiKey: false));

        Assert.Contains("API key", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://10.0.0.5/v1")]
    [InlineData("https://127.0.0.1:8080/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void AnAddressOnTheHostsOwnNetworkIsRefusedWhereTheInstallationDoesNotPermitIt(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AnthropicProviderDriver.ApiKeyAuth, HasApiKey: true));

        Assert.Contains("private", refusal, StringComparison.Ordinal);
    }

    // The installation's opt-in reaches a proxy inside the operator's own network. The family is told what the
    // installation permits on the target, because it is constructed with no arguments and has no other way to
    // learn it.
    [Fact]
    public void AnAddressOnTheHostsOwnNetworkIsTakenWhereTheInstallationPermitsIt()
    {
        var target = new AiProbeTarget("https://10.0.0.5/v1", AnthropicProviderDriver.ApiKeyAuth, HasApiKey: true)
        {
            AllowsPrivateAddress = true,
        };

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    [Fact]
    public void PlainHttpIsRefusedWhereTheInstallationDoesNotPermitIt()
    {
        var target = new AiProbeTarget("http://api.anthropic.com/v1", AnthropicProviderDriver.ApiKeyAuth, HasApiKey: true);

        Assert.Contains("https", Driver().ValidateProbeTarget(target), StringComparison.Ordinal);
        Assert.Null(Driver().ValidateProbeTarget(target with { AllowsInsecureScheme = true }));
    }

    // The opt-in covers where a request may go, not whether it is encrypted.
    [Fact]
    public void PermittingAPrivateAddressDoesNotPermitPlainHttp()
    {
        var target = new AiProbeTarget("http://10.0.0.5/v1", AnthropicProviderDriver.ApiKeyAuth, HasApiKey: true)
        {
            AllowsPrivateAddress = true,
        };

        Assert.Contains("https", Driver().ValidateProbeTarget(target), StringComparison.Ordinal);
    }

    // The identity key is what every connection of this family is stored against, so it is pinned:
    // changing it silently reinterprets every row that carries it.
    [Fact]
    public void TheDriverDeclaresTheIdentityItsConnectionsAreStoredUnder()
    {
        Assert.Equal("meisterdev/anthropic", Driver().Declaration.Key);
    }

    // Where the credential goes is the provider's rule, not a choice: a bearer token is a 401 here. This is what
    // makes the header something the family applies rather than a credential shape an operator picks.
    [Fact]
    public async Task TheCredentialIsSentInTheHeaderAnthropicReadsAndNeverAsABearerToken()
    {
        var authMode = AnthropicProviderDriver.ApiKeyAuth;

        var wire = new FakeAnthropicEndpoint(
            HttpStatusCode.OK,
            """{"id":"msg_1","model":"claude-opus-5","stop_reason":"end_turn","content":[{"type":"text","text":"ok"}]}""");

        using var chat = Driver().CreateChatClient(EndpointOn(wire, authMode), Model(), ProviderDeclaredProtocolModes.Auto);
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var sent = Assert.Single(wire.Requests);
        Assert.Equal("sk-ant-key", sent.Headers.GetValues("x-api-key").Single());
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task TheModelListIsReadFromTheEndpointsOwnPathWithTheHeadersAnthropicRequires()
    {
        var wire = new FakeAnthropicEndpoint(
            HttpStatusCode.OK,
            """{"data":[{"id":"claude-opus-5"},{"id":"claude-haiku-5"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("succeeded", discovered.DiscoveryStatus);
        Assert.Equal(["claude-haiku-5", "claude-opus-5"], discovered.Models.Select(model => model.RemoteModelId));

        var request = Assert.Single(wire.Requests);
        Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
        Assert.Equal("sk-ant-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(
            AnthropicMessagesChatClient.AnthropicVersion,
            request.Headers.GetValues("anthropic-version").Single());
    }

    // Anthropic serves no embedding models and no structured-output mode, so every discovered model is offered
    // for chat on the Messages protocol and nothing else.
    [Fact]
    public async Task ADiscoveredModelIsOfferedOnlyTheProtocolThisFamilySpeaks()
    {
        var wire = new FakeAnthropicEndpoint(HttpStatusCode.OK, """{"data":[{"id":"claude-opus-5"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        var model = Assert.Single(discovered.Models);
        Assert.Equal([AiOperationKind.Chat], model.OperationKinds);
        Assert.Equal([ProviderDeclaredProtocolModes.Auto, AnthropicProviderDriver.MessagesProtocol], model.SupportedProtocolModes);
    }

    [Fact]
    public async Task AnEndpointThatAnswersTheModelListIsVerified()
    {
        var wire = new FakeAnthropicEndpoint(HttpStatusCode.OK, """{"data":[{"id":"claude-opus-5"}]}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Verified, verification.Status);
    }

    [Fact]
    public async Task AnEndpointThatRefusesTheCredentialReportsWhatItSaid()
    {
        var wire = new FakeAnthropicEndpoint(
            HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
        Assert.Contains("invalid x-api-key", verification.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverThatCannotEmbedSaysSoWithSomethingActionable()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Driver().CreateEmbeddingGenerator(
            EndpointOn(null), Model(), ProviderDeclaredProtocolModes.Auto, 1536));

        Assert.Contains("embedding", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWireProtocolThisFamilyDoesNotSpeakIsRefusedByName()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Driver().CreateChatClient(EndpointOn(null), Model(), AnotherFamilysProtocol));

        Assert.Contains(AnotherFamilysProtocol, failure.Message, StringComparison.Ordinal);
    }

    // A host builds a client to describe this family as well as to call it, so building one without a transport
    // has to succeed. Calling one does not: a client that reached the network on its own would not carry the
    // host's connect-time address check.
    [Fact]
    public async Task ACallWithNoHostTransportRefusesInsteadOfReachingTheNetwork()
    {
        using var chat = Driver().CreateChatClient(EndpointOn(null), Model(), AnthropicProviderDriver.MessagesProtocol);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("client factory", failure.Message, StringComparison.Ordinal);
    }

    // 529 is Anthropic's own overload signal and sits outside the range a generic 5xx rule covers, so retrying
    // it has to be decided here or the most retryable failure the provider produces would be given up on.
    [Fact]
    public void ItsOwnOverloadSignalIsTreatedAsWorthRetrying()
    {
        var verdict = Driver().ClassifyRuntimeFailure(new HttpRequestException("overloaded", null, (HttpStatusCode)529));

        Assert.True(verdict.IsTransient);
        Assert.Equal(529, verdict.HttpStatus);
    }

    // The call reports its failure through a task boundary, so the signal has to be found under a wrapper too.
    [Fact]
    public void AWrappedOverloadSignalIsStillFound()
    {
        var wrapped = new InvalidOperationException(
            "the call failed",
            new HttpRequestException("overloaded", null, (HttpStatusCode)529));

        Assert.True(Driver().ClassifyRuntimeFailure(wrapped).IsTransient);
    }

    // Everything the family has no rule of its own for is left to the shared rule, so the override narrows
    // nothing.
    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(400, false)]
    public void EveryOtherFailureIsLeftToTheSharedRule(int status, bool isTransient)
    {
        var verdict = Driver().ClassifyRuntimeFailure(new HttpRequestException("provider said no", null, (HttpStatusCode)status));

        Assert.Equal(isTransient, verdict.IsTransient);
    }

    [Fact]
    public void CachingIsClaimedBecauseTheNativeClientActuallyMarksABreakpoint()
    {
        Assert.True(Driver().GetChatRuntimeCapabilities(EndpointOn(null), Model(), ProviderDeclaredProtocolModes.Auto).SupportsPromptCaching);
    }

    private static ProviderModelDescriptor Model()
    {
        return new ProviderModelDescriptor(
            Guid.NewGuid(),
            "claude-opus-5",
            [ProviderDeclaredProtocolModes.Auto, AnthropicProviderDriver.MessagesProtocol]);
    }

    // The transport rides on the endpoint, because the driver is constructed once for the family and every
    // connection of it is reached through the client factory the host attaches to that connection.
    private static ProviderEndpoint EndpointOn(HttpMessageHandler? wire, string authMode = AnthropicProviderDriver.ApiKeyAuth)
    {
        return new ProviderEndpoint("meisterdev/anthropic", VendorEndpoint, authMode, "sk-ant-key")
        {
            HostContext = wire is null ? null : new FakeProviderHostContext(wire),
        };
    }

    private static AnthropicProviderDriver Driver()
    {
        return new AnthropicProviderDriver();
    }

    /// <summary>Answers every request the same way and keeps what was sent.</summary>
    private sealed class FakeAnthropicEndpoint(HttpStatusCode status, string body) : HttpMessageHandler
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
