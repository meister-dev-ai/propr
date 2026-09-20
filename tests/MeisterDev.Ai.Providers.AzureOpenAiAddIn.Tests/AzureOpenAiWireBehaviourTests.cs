// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Azure.Core;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.AzureOpenAiAddIn.Tests;

/// <summary>
///     Pins what the Azure driver puts on the wire against a fake endpoint: the address each operation is sent
///     to, the header the credential travels in for each authentication mode, and the place the deployment name
///     occupies in the request.
/// </summary>
/// <remarks>
///     An Azure OpenAI resource serves an OpenAI-compatible surface under <c>/openai/v1/</c> that takes the
///     deployment as the model of the request and needs no api-version parameter. The driver is built on those
///     three facts, and confirming them otherwise would take a live Azure resource.
/// </remarks>
public sealed class AzureOpenAiWireBehaviourTests
{
    private const string ResourceRoot = "https://contoso.openai.azure.com/";
    private const string Deployment = "my-gpt-deployment";

    private const string ChatCompletionBody =
        """
        {
          "id": "chatcmpl-fake", "object": "chat.completion", "created": 1770000000, "model": "m",
          "choices": [{ "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "hi" } }],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;

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

    private const string EmbeddingBody =
        """
        {
          "object": "list", "model": "m",
          "data": [{ "object": "embedding", "index": 0, "embedding": [0.1, 0.2] }],
          "usage": { "prompt_tokens": 1, "total_tokens": 1 }
        }
        """;

    private const string ModelListBody =
        """
        { "object": "list", "data": [{ "id": "gpt-5-mini", "object": "model", "created": 1, "owned_by": "azure" }] }
        """;

    // The deployment is addressed as the model of the request. A dated Azure surface takes it as a path segment
    // instead, so this assertion is what catches the call being routed that way.
    [Fact]
    public async Task AChatCompletionAddressesTheV1SurfaceAndCarriesTheDeploymentAsTheModel()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ChatCompletionBody);

        await ChatClient(wire, AzureOpenAiProviderDriver.ChatCompletionsProtocol).GetResponseAsync("hello");

        Assert.Equal(
            new Uri("https://contoso.openai.azure.com/openai/v1/chat/completions"),
            Assert.Single(wire.RequestUris));
        Assert.Contains($"\"model\":\"{Deployment}\"", Assert.Single(wire.RequestBodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResponsesCallAddressesTheV1SurfaceAndCarriesTheDeploymentAsTheModel()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ResponseBody);

        await ChatClient(wire, AzureOpenAiProviderDriver.ResponsesProtocol).GetResponseAsync("hello");

        Assert.Equal(
            new Uri("https://contoso.openai.azure.com/openai/v1/responses"),
            Assert.Single(wire.RequestUris));
        Assert.Contains($"\"model\":\"{Deployment}\"", Assert.Single(wire.RequestBodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmbeddingCallAddressesTheV1SurfaceAndCarriesTheDeploymentAsTheModel()
    {
        var wire = new FakeCompatibleEndpoint().Responds(EmbeddingBody);
        var driver = Driver(wire);

        await driver
            .CreateEmbeddingGenerator(
                Endpoint(AzureOpenAiProviderDriver.ApiKeyAuth, wire), Model("my-embedding-deployment"), ProviderDeclaredProtocolModes.Embeddings, 1536)
            .GenerateAsync(["hello"]);

        Assert.Equal(
            new Uri("https://contoso.openai.azure.com/openai/v1/embeddings"),
            Assert.Single(wire.RequestUris));
        Assert.Contains("\"model\":\"my-embedding-deployment\"", Assert.Single(wire.RequestBodies), StringComparison.Ordinal);
    }

    // Discovery reads the resource's own model list off the same surface, so a connection can be verified without
    // the operator naming a deployment first.
    [Fact]
    public async Task DiscoveryReadsTheModelListFromTheV1Surface()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ModelListBody);

        var discovery = await Driver(wire).DiscoverModelsAsync(Endpoint(AzureOpenAiProviderDriver.ApiKeyAuth, wire));

        Assert.Equal("succeeded", discovery.DiscoveryStatus);
        Assert.Equal("gpt-5-mini", Assert.Single(discovery.Models).RemoteModelId);
        Assert.Equal(new Uri("https://contoso.openai.azure.com/openai/v1/models"), Assert.Single(wire.RequestUris));
    }

    // Verification gates whether a profile may be used, and discovery reports a refusal as a failed result
    // instead of throwing. Reading the model list through discovery would therefore report every refusal as a
    // verified connection carrying a warning.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiVerificationFailureCategory.Credentials)]
    [InlineData(HttpStatusCode.Forbidden, AiVerificationFailureCategory.Authorization)]
    public async Task ARefusedCredentialFailsVerification(
        HttpStatusCode status,
        AiVerificationFailureCategory expectedCategory)
    {
        var wire = new FakeCompatibleEndpoint()
            .Responds(status, """{ "error": { "code": "401", "message": "Access denied." } }""");

        var result = await Driver(wire).VerifyAsync(Endpoint(AzureOpenAiProviderDriver.ApiKeyAuth, wire));

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Equal(expectedCategory, result.FailureCategory);
        Assert.Equal(((int)status).ToString(), result.DriverMetadata?["httpStatus"]);
    }

    [Fact]
    public async Task AResourceThatAnswersItsModelListVerifies()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ModelListBody);

        var result = await Driver(wire).VerifyAsync(Endpoint(AzureOpenAiProviderDriver.ApiKeyAuth, wire));

        Assert.Equal(AiVerificationStatus.Verified, result.Status);
        Assert.Empty(result.Warnings ?? []);
    }

    // The v1 surface reads a resource key from its own header. Sending it as a bearer instead would be accepted
    // too, but then a key and an Entra token would be indistinguishable on the wire.
    [Fact]
    public async Task AStoredKeyTravelsInTheHeaderTheResourceReadsItFrom()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ChatCompletionBody);

        await ChatClient(wire, AzureOpenAiProviderDriver.ChatCompletionsProtocol).GetResponseAsync("hello");

        var headers = Assert.Single(wire.RequestHeaders);
        Assert.Equal("azure-resource-key", headers["api-key"]);
        Assert.Null(Assert.Single(wire.AuthorizationHeaders));
    }

    // The managed-identity mode has no key to send: it mints a token per request from the Azure credential chain
    // and presents it as a bearer, which is the other scheme the resource accepts.
    [Fact]
    public async Task AManagedIdentityPresentsAMintedTokenAsABearer()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ChatCompletionBody);
        var credential = new RecordingTokenCredential("entra-access-token");

        await ChatClient(wire, AzureOpenAiProviderDriver.ChatCompletionsProtocol, AzureOpenAiProviderDriver.AzureIdentityAuth, credential)
            .GetResponseAsync("hello");

        Assert.Equal("Bearer entra-access-token", Assert.Single(wire.AuthorizationHeaders));
        Assert.DoesNotContain("api-key", Assert.Single(wire.RequestHeaders).Keys, StringComparer.OrdinalIgnoreCase);
    }

    // A token is only accepted by the resource when it was requested for the resource's own audience, so the
    // scope the driver asks for is part of the contract rather than an implementation detail.
    [Fact]
    public async Task AManagedIdentityTokenIsRequestedForTheResourceScope()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ChatCompletionBody);
        var credential = new RecordingTokenCredential("entra-access-token");

        await ChatClient(wire, AzureOpenAiProviderDriver.ChatCompletionsProtocol, AzureOpenAiProviderDriver.AzureIdentityAuth, credential)
            .GetResponseAsync("hello");

        Assert.Equal(
            ["https://cognitiveservices.azure.com/.default"],
            Assert.Single(credential.RequestedScopes));
    }

    // The v1 surface defaults to v1, so nothing has to be pinned in the query string. A stray api-version would
    // route the call to a dated surface that addresses deployments differently.
    [Fact]
    public async Task NoApiVersionIsPinnedOnTheQueryString()
    {
        var wire = new FakeCompatibleEndpoint().Responds(ChatCompletionBody);

        await ChatClient(wire, AzureOpenAiProviderDriver.ChatCompletionsProtocol).GetResponseAsync("hello");

        Assert.Equal(string.Empty, Assert.Single(wire.RequestUris)!.Query);
    }

    // An Azure AI Foundry portal URL carries a project path that is not part of the API surface, so the driver
    // has to resolve back to the resource root before appending the surface.
    [Theory]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.openai.azure.com")]
    [InlineData("https://contoso.openai.azure.com/api/projects/my-project")]
    public async Task TheConfiguredUrlIsResolvedBackToTheResourcesOwnSurface(string baseUrl)
    {
        var wire = new FakeCompatibleEndpoint().Responds(ChatCompletionBody);
        var endpoint = Endpoint(AzureOpenAiProviderDriver.ApiKeyAuth, wire, baseUrl);

        await Driver(wire)
            .CreateChatClient(endpoint, Model(Deployment), AzureOpenAiProviderDriver.ChatCompletionsProtocol)
            .GetResponseAsync("hello");

        Assert.Equal(
            new Uri("https://contoso.openai.azure.com/openai/v1/chat/completions"),
            Assert.Single(wire.RequestUris));
    }

    private static IChatClient ChatClient(
        FakeCompatibleEndpoint wire,
        string protocolMode,
        string authMode = AzureOpenAiProviderDriver.ApiKeyAuth,
        TokenCredential? credential = null)
    {
        return Driver(wire, credential).CreateChatClient(Endpoint(authMode, wire), Model(Deployment), protocolMode);
    }

    private static AzureOpenAiProviderDriver Driver(FakeCompatibleEndpoint wire, TokenCredential? credential = null)
    {
        _ = wire;
        return new AzureOpenAiProviderDriver(credential);
    }

    // The transport rides on the endpoint, which is how a family loaded from a directory is handed one at all.
    private static ProviderEndpoint Endpoint(string authMode, FakeCompatibleEndpoint wire, string? baseUrl = null)
    {
        return new ProviderEndpoint(
            "meisterdev/azureOpenAi",
            baseUrl ?? ResourceRoot,
            authMode,
            authMode == AzureOpenAiProviderDriver.AzureIdentityAuth ? null : "azure-resource-key")
        {
            HostContext = new FakeProviderHostContext(wire),
        };
    }

    private static ProviderModelDescriptor Model(string deployment)
    {
        return new ProviderModelDescriptor(
            Guid.NewGuid(),
            deployment,
            [
                ProviderDeclaredProtocolModes.Auto, AzureOpenAiProviderDriver.ResponsesProtocol, AzureOpenAiProviderDriver.ChatCompletionsProtocol,
                ProviderDeclaredProtocolModes.Embeddings
            ]);
    }

    /// <summary>
    ///     Stands in for the Azure credential chain so the managed-identity mode can be exercised without an
    ///     Azure tenant, and records the scope each token was asked for.
    /// </summary>
    private sealed class RecordingTokenCredential(string token) : TokenCredential
    {
        public List<string[]> RequestedScopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            this.RequestedScopes.Add(requestContext.Scopes);
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            return new ValueTask<AccessToken>(this.GetToken(requestContext, cancellationToken));
        }
    }
}
