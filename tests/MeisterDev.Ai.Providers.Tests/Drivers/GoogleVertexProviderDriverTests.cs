// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Covers the Google driver across both of its surfaces: what it accepts as an endpoint, what it makes of
///     the Gemini API's model list, and what it says when Vertex has no list to give.
/// </summary>
public sealed class GoogleVertexProviderDriverTests
{
    [Fact]
    public void TheGeminiApiEndpointIsAccepted()
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget("https://generativelanguage.googleapis.com", AiAuthMode.ApiKey, HasApiKey: true)));
    }

    // On Vertex the location is part of the host, which is what makes a project's residency visible in the URL
    // instead of hidden in a setting that could disagree with it.
    [Fact]
    public void AVertexEndpointMustNameItsLocation()
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget("https://europe-west4-aiplatform.googleapis.com", AiAuthMode.GcpAdc, HasApiKey: true)));

        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("https://aiplatform.googleapis.com", AiAuthMode.GcpAdc, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("location", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHostOutsideGoogleIsRefusedUnlessPrivateEgressIsPermitted()
    {
        var target = new AiProbeTarget("https://gemini.internal.example.com", AiAuthMode.ApiKey, HasApiKey: true);

        Assert.NotNull(Driver().ValidateProbeTarget(target));
        Assert.Null(Driver(allowPrivateEgress: true).ValidateProbeTarget(target));
    }

    [Fact]
    public async Task TheGeminiApiModelListIsSortedIntoWhatEachModelCanDo()
    {
        var wire = new FakeGoogleApi(
            HttpStatusCode.OK,
            """
            {"models":[
              {"name":"models/gemini-3-pro","displayName":"Gemini 3 Pro","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/text-embedding-005","displayName":"Text Embedding 005","supportedGenerationMethods":["embedContent"]},
              {"name":"models/imagen-4.0","displayName":"Imagen 4","supportedGenerationMethods":["predict"]}]}
            """);

        var result = await Driver(wire).DiscoverModelsAsync(GeminiEndpoint());

        Assert.Equal("succeeded", result.DiscoveryStatus);

        // The provider qualifies its ids; carrying the prefix through would address "models/models/…" later.
        var chat = result.Models.Single(model => model.RemoteModelId == "gemini-3-pro");
        Assert.Contains(AiOperationKind.Chat, chat.OperationKinds);
        Assert.True(chat.SupportsToolUse);

        var embedding = result.Models.Single(model => model.RemoteModelId == "text-embedding-005");
        Assert.Contains(AiOperationKind.Embedding, embedding.OperationKinds);

        // A model that neither answers nor embeds cannot serve a review, so offering it would only produce a
        // call that fails later.
        Assert.DoesNotContain(result.Models, model => model.RemoteModelId.StartsWith("imagen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGoogleRejectionIsReportedInGooglesOwnWords()
    {
        var wire = new FakeGoogleApi(
            HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"Generative Language API has not been used in project 42 before","status":"PERMISSION_DENIED"}}""");

        var result = await Driver(wire).VerifyAsync(GeminiEndpoint());

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("has not been used in project", result.Summary, StringComparison.Ordinal);
    }

    // Vertex publishes no model list on the inference surface, so discovery reports that rather than failing —
    // manual entry is how a Vertex profile names its models.
    [Fact]
    public async Task VertexReportsThatItsModelsAreEnteredByHand()
    {
        var result = await Driver().DiscoverModelsAsync(VertexEndpoint());

        Assert.Equal("succeeded", result.DiscoveryStatus);
        Assert.Empty(result.Models);
        Assert.Contains(result.Warnings, warning => warning.Contains("enter the model", StringComparison.OrdinalIgnoreCase));
    }

    // Without a project there is nothing to address on Vertex, and the failure would otherwise arrive as a
    // malformed-URL rejection from Google on the first review.
    [Fact]
    public async Task AVertexProfileWithoutAProjectIsRefusedAtConfigurationTime()
    {
        var endpoint = VertexEndpoint() with { DefaultQueryParams = null };

        var result = await Driver().VerifyAsync(endpoint);

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("project", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CachingIsClaimedBecauseGeminiDoesItWithoutBeingAsked()
    {
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "gemini-3-pro", [AiProtocolMode.Auto]);

        Assert.True(Driver().GetChatRuntimeCapabilities(GeminiEndpoint(), model, AiProtocolMode.Auto).SupportsPromptCaching);
    }

    private static ProviderEndpoint GeminiEndpoint()
    {
        return new ProviderEndpoint(
            AiProviderKind.GoogleVertex,
            "https://generativelanguage.googleapis.com",
            AiAuthMode.ApiKey,
            "gemini-key");
    }

    private static ProviderEndpoint VertexEndpoint()
    {
        return new ProviderEndpoint(
            AiProviderKind.GoogleVertex,
            "https://europe-west4-aiplatform.googleapis.com",
            AiAuthMode.GcpAdc,
            "{}")
        {
            DefaultQueryParams = new Dictionary<string, string> { ["project"] = "meister-dev-prod" },
        };
    }

    private static GoogleVertexProviderDriver Driver(HttpMessageHandler? wire = null, bool allowPrivateEgress = false)
    {
        var services = new ServiceCollection();
        var admin = services.AddHttpClient("AiProviderAdmin");
        var runtime = services.AddHttpClient("AiProviderRuntime");
        if (wire is not null)
        {
            admin.ConfigurePrimaryHttpMessageHandler(() => wire);
            runtime.ConfigurePrimaryHttpMessageHandler(() => wire);
        }

        var provider = services.BuildServiceProvider();

        return new GoogleVertexProviderDriver(
            provider.GetRequiredService<IHttpClientFactory>(),
            new StubCredentials(),
            allowPrivateEgress,
            allowInsecureScheme: false);
    }

    /// <summary>Answers every request the same way, which is all these assertions need of it.</summary>
    private sealed class FakeGoogleApi(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    /// <summary>Stands in for a Google credential; minting a real one would need a Google account.</summary>
    private sealed class StubCredentials : IGoogleCredentialSource
    {
        public Task AuthenticateAsync(
            HttpRequestMessage request,
            ProviderEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            request.Headers.TryAddWithoutValidation(GoogleCredentialSource.ApiKeyHeaderName, "stub");
            return Task.CompletedTask;
        }
    }
}
