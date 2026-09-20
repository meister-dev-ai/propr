// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.OpenAiAddIn.Tests;

/// <summary>
///     This family's own rules: which endpoints it takes, what it makes of a model list, and what it does when
///     the host hands it no way out to the network.
/// </summary>
/// <remarks>
///     What separates this family from the compatible one is a single rule: an Azure-hosted endpoint is refused
///     rather than served. Refusing is the useful behaviour even though the request would partly work, because
///     Azure authenticates differently and can use a managed identity instead of a key, so a profile stored here
///     would either fail on its first call or lock the operator out of the keyless option.
/// </remarks>
public sealed class OpenAiProviderDriverTests
{
    // A wire shape another family declares, which a binding holds after a connection is repointed and
    // is a shape this family cannot serve.
    private const string AnotherFamilysProtocol = "meisterdev/anthropic:AnthropicMessages";

    private const string VendorEndpoint = "https://api.openai.com/v1";

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/")]
    public void TheVendorEndpointIsAccepted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, OpenAiProviderDriver.ApiKeyAuth, true)));
    }

    [Theory]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com/")]
    [InlineData("https://contoso.cognitiveservices.azure.com/")]
    [InlineData("https://contoso.openai.azure.com./")]
    public void AnAzureHostIsRefusedAndNamesTheProviderKindThatFits(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, OpenAiProviderDriver.ApiKeyAuth, true));

        Assert.Contains("azureOpenAi", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileWithNoCredentialIsRefusedWhileTheOperatorIsStillConfiguringIt()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(VendorEndpoint, OpenAiProviderDriver.ApiKeyAuth, false));

        Assert.Contains("API key", refusal, StringComparison.OrdinalIgnoreCase);
    }

    // The vendor endpoint is what separates this family from the compatible one, so a base URL anywhere else is
    // refused and the refusal names where it belongs. An address on the host's own network reaches here too, and
    // the installation's private-egress opt-in does not admit it: the opt-in says where this installation may
    // reach, and this family says where it goes.
    [Theory]
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://10.0.0.5/v1")]
    [InlineData("https://gateway.example.net/v1")]
    [InlineData("https://sub.api.openai.com/v1")]
    [InlineData("https://api.openai.com.evil.example/v1")]
    public void AnEndpointOtherThanTheVendorsIsRefusedAndNamesTheFamilyThatFits(string baseUrl)
    {
        var target = new AiProbeTarget(baseUrl, OpenAiProviderDriver.ApiKeyAuth, true) { AllowsPrivateAddress = true };

        var refusal = Driver().ValidateProbeTarget(target);

        Assert.Contains("api.openai.com", refusal, StringComparison.Ordinal);
        Assert.Contains("OpenAI-compatible", refusal, StringComparison.Ordinal);
    }

    // A trailing dot is a legitimate spelling of the same name and reaches the same host.
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com./v1")]
    [InlineData("https://API.OpenAI.com/v1")]
    public void TheVendorEndpointIsTakenHoweverItIsSpelled(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, OpenAiProviderDriver.ApiKeyAuth, true)));
    }

    [Fact]
    public void PlainHttpIsRefusedWhereTheInstallationDoesNotPermitIt()
    {
        var target = new AiProbeTarget("http://api.openai.com/v1", OpenAiProviderDriver.ApiKeyAuth, true);

        Assert.Contains("https", Driver().ValidateProbeTarget(target), StringComparison.Ordinal);
        Assert.Null(Driver().ValidateProbeTarget(target with { AllowsInsecureScheme = true }));
    }

    [Fact]
    public void ACredentialInTheAddressIsRefused()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("https://user:secret@api.openai.com/v1", OpenAiProviderDriver.ApiKeyAuth, true));

        Assert.Contains("credential", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressThatIsNotAUrlIsRefused()
    {
        Assert.NotNull(Driver().ValidateProbeTarget(new AiProbeTarget("not-a-url", OpenAiProviderDriver.ApiKeyAuth, true)));
    }

    // The identity key is what every connection of this family is stored against, so it is pinned:
    // changing it silently reinterprets every row that carries it.
    [Fact]
    public void TheDriverDeclaresTheIdentityItsConnectionsAreStoredUnder()
    {
        Assert.Equal("meisterdev/openAi", Driver().Declaration.Key);
    }

    [Fact]
    public async Task TheModelListIsReadFromTheEndpointsOwnPath()
    {
        var wire = new FakeVendorEndpoint(HttpStatusCode.OK, """{"data":[{"id":"gpt-5"},{"id":"gpt-5-mini"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("succeeded", discovered.DiscoveryStatus);
        Assert.Equal(["gpt-5", "gpt-5-mini"], discovered.Models.Select(model => model.RemoteModelId));

        var request = Assert.Single(wire.Requests);
        Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test-key", request.Headers.Authorization.Parameter);
    }

    // The vendor lists identifiers and no capabilities, so what a model is offered as is read off its identifier
    // and corrected by the operator.
    [Fact]
    public async Task AModelNamedAsAnEmbeddingIsOfferedAsOne()
    {
        var wire = new FakeVendorEndpoint(HttpStatusCode.OK, """{"data":[{"id":"text-embedding-3-small"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        var model = Assert.Single(discovered.Models);
        Assert.Equal([AiOperationKind.Embedding], model.OperationKinds);
        Assert.Equal([ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings], model.SupportedProtocolModes);
    }

    // The vendor's list holds more than the models a chat or an embedding client can call. Offered as chat
    // models, they are bindings an operator can pick and a review then fails on, with capabilities asserted for
    // them that no image or speech model has.
    [Fact]
    public async Task ModelsThisHostBindsToNoOperationAreLeftOutAndCounted()
    {
        var wire = new FakeVendorEndpoint(
            HttpStatusCode.OK,
            """{"data":[{"id":"gpt-5"},{"id":"dall-e-3"},{"id":"tts-1-hd"},{"id":"whisper-1"},{"id":"omni-moderation-latest"},{"id":"gpt-image-1"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("succeeded", discovered.DiscoveryStatus);
        Assert.Equal(["gpt-5"], discovered.Models.Select(model => model.RemoteModelId));
        Assert.Contains(discovered.Warnings, warning => warning.Contains("5 listed model(s)", StringComparison.Ordinal));
    }

    // The Responses API is this vendor's own surface, so a chat model discovered here is offered it — which
    // the compatible family cannot assume of an arbitrary server.
    [Fact]
    public async Task ADiscoveredChatModelIsOfferedTheResponsesApi()
    {
        var wire = new FakeVendorEndpoint(HttpStatusCode.OK, """{"data":[{"id":"gpt-5"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Contains(OpenAiProviderDriver.ResponsesProtocol, Assert.Single(discovered.Models).SupportedProtocolModes);
    }

    [Fact]
    public async Task AnEndpointThatAnswersTheModelListIsVerified()
    {
        var wire = new FakeVendorEndpoint(HttpStatusCode.OK, """{"data":[{"id":"gpt-5"}]}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Verified, verification.Status);
    }

    // A model list longer than the host reads cannot be parsed, and presenting the part that was read as the
    // account's own list would be wrong. Both callers of the listing read a refusal and neither catches an
    // exception, so it is reported as one rather than raised.
    [Fact]
    public async Task AModelListLongerThanTheHostReadsIsAFailedDiscovery()
    {
        var wire = new FakeVendorEndpoint(
            HttpStatusCode.OK,
            $$"""{"data":[{"id":"{{new string('m', 5 * 1024 * 1024)}}"}]}""");

        var discovered = await Driver().DiscoverModelsAsync(EndpointOn(wire));

        Assert.Equal("failed", discovered.DiscoveryStatus);
        Assert.Empty(discovered.Models);
    }

    [Fact]
    public async Task AModelListLongerThanTheHostReadsIsAFailedVerification()
    {
        var wire = new FakeVendorEndpoint(
            HttpStatusCode.OK,
            $$"""{"data":[{"id":"{{new string('m', 5 * 1024 * 1024)}}"}]}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
    }

    [Fact]
    public async Task AnEndpointThatRefusesTheCredentialReportsWhatItSaid()
    {
        var wire = new FakeVendorEndpoint(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key."}}""");

        var verification = await Driver().VerifyAsync(EndpointOn(wire));

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
        Assert.Contains("Incorrect API key.", verification.Summary, StringComparison.Ordinal);
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
    // host's connect-time address check.
    [Fact]
    public async Task ACallWithNoHostTransportRefusesInsteadOfReachingTheNetwork()
    {
        using var chat = Driver().CreateChatClient(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [OpenAiProviderDriver.ChatCompletionsProtocol]),
            OpenAiProviderDriver.ChatCompletionsProtocol);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("client factory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmbeddingCallWithNoHostTransportRefusesInsteadOfReachingTheNetwork()
    {
        using var embeddings = Driver().CreateEmbeddingGenerator(
            EndpointOn(null),
            new ProviderModelDescriptor(Guid.NewGuid(), "text-embedding-3-small", [ProviderDeclaredProtocolModes.Embeddings]),
            ProviderDeclaredProtocolModes.Embeddings,
            1536);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => embeddings.GenerateAsync(["hello"]));

        Assert.Contains("client factory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChatCompletionsCallGoesOutOnTheHostsTransport()
    {
        var wire = new FakeVendorEndpoint(
            HttpStatusCode.OK,
            """{"id":"1","object":"chat.completion","created":0,"model":"gpt-5","choices":[]}""");

        using var chat = Driver().CreateChatClient(
            EndpointOn(wire),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [OpenAiProviderDriver.ChatCompletionsProtocol]),
            OpenAiProviderDriver.ChatCompletionsProtocol);

        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal("/v1/chat/completions", Assert.Single(wire.Requests).RequestUri!.AbsolutePath);
    }

    // The other surface this family speaks, and the one the capability report turns on.
    [Fact]
    public async Task AResponsesCallGoesOutOnTheHostsTransport()
    {
        var wire = new FakeVendorEndpoint(
            HttpStatusCode.OK,
            """{"id":"resp_1","object":"response","created_at":0,"model":"gpt-5","status":"completed","output":[]}""");

        using var chat = Driver().CreateChatClient(
            EndpointOn(wire),
            new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5", [OpenAiProviderDriver.ResponsesProtocol]),
            OpenAiProviderDriver.ResponsesProtocol);

        await Record.ExceptionAsync(() => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal("/v1/responses", Assert.Single(wire.Requests).RequestUri!.AbsolutePath);
    }

    // Provider-managed sessions and background responses are affordances of the Responses surface, so the client
    // built on the other surface claims none of them.
    [Theory]
    [InlineData(OpenAiProviderDriver.ResponsesProtocol, true)]
    [InlineData(ProviderDeclaredProtocolModes.Auto, true)]
    [InlineData(OpenAiProviderDriver.ChatCompletionsProtocol, false)]
    public void TheCapabilitiesReportedFollowTheWireShapeTheClientWasBuiltOn(string protocolMode, bool onResponses)
    {
        var model = new ProviderModelDescriptor(
            Guid.NewGuid(),
            "gpt-5",
            [ProviderDeclaredProtocolModes.Auto, OpenAiProviderDriver.ResponsesProtocol, OpenAiProviderDriver.ChatCompletionsProtocol]);

        var capabilities = Driver().GetChatRuntimeCapabilities(EndpointOn(null), model, protocolMode);

        Assert.Equal(new ProviderRuntimeCapabilities(onResponses, onResponses, onResponses, onResponses), capabilities);
    }

    // The transport rides on the endpoint, because the driver is constructed once for the family and every
    // connection of it is reached through the client factory the host attaches to that connection.
    private static ProviderEndpoint EndpointOn(HttpMessageHandler? wire)
    {
        return new ProviderEndpoint("meisterdev/openAi", VendorEndpoint, OpenAiProviderDriver.ApiKeyAuth, "sk-test-key")
        {
            HostContext = wire is null ? null : new FakeProviderHostContext(wire),
        };
    }

    private static OpenAiProviderDriver Driver()
    {
        return new OpenAiProviderDriver();
    }

    /// <summary>Answers every request the same way and keeps what was sent.</summary>
    private sealed class FakeVendorEndpoint(HttpStatusCode status, string body) : HttpMessageHandler
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
