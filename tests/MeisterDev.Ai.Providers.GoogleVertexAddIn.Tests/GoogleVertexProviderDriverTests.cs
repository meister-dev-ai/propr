// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2.Responses;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn.Tests;

/// <summary>
///     Covers the Google driver across both of its surfaces: what it accepts as an endpoint, what it makes of
///     the Gemini API's model list, and what it says when Vertex has no list to give.
/// </summary>
public sealed class GoogleVertexProviderDriverTests
{
    // What an operator sees where each credential shape is offered, which is how a refusal names them.
    private const string ApiKeyLabel = "API Key";

    private const string GcpAdcLabel = "Google Application Default Credentials";

    [Fact]
    public void TheGeminiApiEndpointIsAccepted()
    {
        Assert.Null(
            Driver().ValidateProbeTarget(
                new AiProbeTarget("https://generativelanguage.googleapis.com", GoogleVertexProviderDriver.ApiKeyAuth, HasApiKey: true)));
    }

    // A Vertex host either names the location inference runs in or names none, and naming none addresses the
    // global endpoint. Both are accepted: Google serves its newest models globally only, so refusing the
    // region-less host would put them out of reach.
    [Theory]
    [InlineData("https://europe-west4-aiplatform.googleapis.com")]
    [InlineData("https://aiplatform.googleapis.com")]
    public void AVertexEndpointIsAcceptedWhetherOrNotItNamesALocation(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, GoogleVertexProviderDriver.GcpAdcAuth, HasApiKey: true)));
    }

    // The two surfaces take different credentials and the URL says which, so a mode that names the other one
    // stores material the surface cannot use. On the Gemini API that is worse than a failed call: the credential
    // goes into a header as though it were a key, so a whole service-account document would be sent there. Both
    // shapes are named the way the form names them, which an operator has to pick between.
    [Fact]
    public void AModeThatDoesNotMatchTheEndpointSurfaceIsRefusedNamingBoth()
    {
        var onVertex = Driver().ValidateProbeTarget(
            new AiProbeTarget(
                "https://europe-west4-aiplatform.googleapis.com",
                GoogleVertexProviderDriver.ApiKeyAuth,
                HasApiKey: true));

        Assert.NotNull(onVertex);
        Assert.Contains("Vertex AI", onVertex, StringComparison.Ordinal);
        Assert.Contains(ApiKeyLabel, onVertex, StringComparison.Ordinal);
        Assert.Contains(GcpAdcLabel, onVertex, StringComparison.Ordinal);

        var onGemini = Driver().ValidateProbeTarget(
            new AiProbeTarget(
                "https://generativelanguage.googleapis.com",
                GoogleVertexProviderDriver.GcpAdcAuth,
                HasApiKey: true));

        Assert.NotNull(onGemini);
        Assert.Contains("Gemini API", onGemini, StringComparison.Ordinal);
        Assert.Contains(GcpAdcLabel, onGemini, StringComparison.Ordinal);
        Assert.Contains(ApiKeyLabel, onGemini, StringComparison.Ordinal);
    }

    // Each surface's own mode stays acceptable, or the refusal above would have closed both of them.
    [Fact]
    public void EachSurfaceAcceptsTheModeThatBelongsToIt()
    {
        Assert.Null(
            Driver().ValidateProbeTarget(
                new AiProbeTarget("https://europe-west4-aiplatform.googleapis.com", GoogleVertexProviderDriver.GcpAdcAuth, HasApiKey: true)));
        Assert.Null(
            Driver().ValidateProbeTarget(
                new AiProbeTarget("https://generativelanguage.googleapis.com", GoogleVertexProviderDriver.ApiKeyAuth, HasApiKey: true)));
    }

    // The declared host patterns are the whole address rule now that the family is loaded from a directory and
    // is told nothing about the installation's egress settings. A literal address on the host's own network and
    // a plain-http URL are refused by the same check, because neither is a Google host reached over https.
    [Fact]
    public void AnAddressOutsideTheDeclaredGoogleHostsIsRefused()
    {
        Assert.NotNull(
            Driver().ValidateProbeTarget(new AiProbeTarget("https://gemini.internal.example.com", GoogleVertexProviderDriver.ApiKeyAuth, HasApiKey: true)));
        Assert.NotNull(
            Driver().ValidateProbeTarget(
                new AiProbeTarget("https://169.254.169.254/latest/meta-data/", GoogleVertexProviderDriver.ApiKeyAuth, HasApiKey: true)));
        Assert.NotNull(
            Driver().ValidateProbeTarget(
                new AiProbeTarget("http://generativelanguage.googleapis.com", GoogleVertexProviderDriver.ApiKeyAuth, HasApiKey: true)));
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

        var result = await Driver().DiscoverModelsAsync(GeminiEndpoint(wire));

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

        var result = await Driver().VerifyAsync(GeminiEndpoint(wire));

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("has not been used in project", result.Summary, StringComparison.Ordinal);
    }

    // A model advertising both methods can do both, and each one carries its own wire shape. Keeping one would
    // leave the model unbindable for the other purpose, because a purpose is bound against what discovery
    // recorded.
    [Fact]
    public async Task AModelAdvertisingBothMethodsKeepsBothPurposesAndBothWireShapes()
    {
        var wire = new FakeGoogleApi(
            HttpStatusCode.OK,
            """
            {"models":[
              {"name":"models/gemini-omni","displayName":"Gemini Omni",
               "supportedGenerationMethods":["generateContent","embedContent"]}]}
            """);

        var result = await Driver().DiscoverModelsAsync(GeminiEndpoint(wire));

        var model = Assert.Single(result.Models);
        Assert.Contains(AiOperationKind.Chat, model.OperationKinds);
        Assert.Contains(AiOperationKind.Embedding, model.OperationKinds);
        Assert.Contains(GoogleVertexProviderDriver.GenerateContentProtocol, model.SupportedProtocolModes);
        Assert.Contains(ProviderDeclaredProtocolModes.Embeddings, model.SupportedProtocolModes);
    }

    // A provider's body is not this driver's to shape. A field holding a number or an object where the driver
    // expects text leaves that value unread and the rest of the answer usable; it does not end the call.
    [Fact]
    public async Task AModelListWhoseFieldsAreNotTextIsReadForWhatItDoesCarry()
    {
        var wire = new FakeGoogleApi(
            HttpStatusCode.OK,
            """
            {"models":[
              {"name":{"value":"models/gemini-3-pro"},"supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3-flash","displayName":42,"supportedGenerationMethods":["generateContent",7]}]}
            """);

        var result = await Driver().DiscoverModelsAsync(GeminiEndpoint(wire));

        Assert.Equal("succeeded", result.DiscoveryStatus);

        // The entry whose name is an object carries no id to address the model by, so it is left out; the entry
        // whose display name is a number keeps its id as its name.
        var model = Assert.Single(result.Models);
        Assert.Equal("gemini-3-flash", model.RemoteModelId);
        Assert.Equal("gemini-3-flash", model.DisplayName);
    }

    // A refusal is reported in the provider's own words where it gave any, and as the status where the message
    // is not text. Reading it must not turn a refusal into an internal error.
    [Fact]
    public async Task ARefusalWhoseMessageIsNotTextIsReportedAsTheStatus()
    {
        var wire = new FakeGoogleApi(HttpStatusCode.Forbidden, """{"error":{"message":{"detail":1}}}""");

        var result = await Driver().VerifyAsync(GeminiEndpoint(wire));

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("403", result.Summary, StringComparison.Ordinal);
    }

    // A proxy or an ingress in front of the API states its error as a string where Google states an object.
    // Reading the object's field off a string would end the read as an internal error.
    [Theory]
    [InlineData("""{"error":"the gateway has no capacity"}""", "the gateway has no capacity")]
    [InlineData("""{"error":["denied"]}""", "403")]
    [InlineData("""{"error":403}""", "403")]
    public async Task ARefusalWhoseErrorIsNotGooglesShapeIsStillAFailedVerification(string body, string expected)
    {
        var wire = new FakeGoogleApi(HttpStatusCode.Forbidden, body);

        var result = await Driver().VerifyAsync(GeminiEndpoint(wire));

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains(expected, result.Summary, StringComparison.Ordinal);
    }

    // A model list longer than the host reads cannot be parsed, and presenting the part that was read as the
    // account's own list would be wrong. It is reported as a failed discovery, the same result every other
    // refusal produces.
    [Fact]
    public async Task AModelListLongerThanTheHostReadsIsAFailedDiscovery()
    {
        var wire = new FakeGoogleApi(
            HttpStatusCode.OK,
            $$"""{"models":[{"name":"models/{{new string('m', 5 * 1024 * 1024)}}"}]}""");

        var result = await Driver().DiscoverModelsAsync(GeminiEndpoint(wire));

        Assert.Equal("failed", result.DiscoveryStatus);
        Assert.Empty(result.Models);
    }

    // An unreachable endpoint is an ordinary misconfiguration: a base URL that resolves to nothing, or egress
    // that blocks it. The operator has to be told that rather than shown an internal error.
    [Fact]
    public async Task AnEndpointThatCannotBeReachedIsAFailedVerification()
    {
        var wire = new UnreachableGoogleApi();

        var result = await Driver().VerifyAsync(GeminiEndpoint(wire));

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("Connection refused", result.Summary, StringComparison.Ordinal);
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

    // A Vertex profile is verified by minting a token, which is a call to Google's token endpoint. A refused
    // credential comes back from that endpoint as its own exception type, and the operator has to read it as a
    // failed verification.
    [Fact]
    public async Task AVertexCredentialTheTokenEndpointRefusesIsAFailedVerification()
    {
        var refused = new TokenResponseException(
            new TokenErrorResponse
            {
                Error = "invalid_grant",
                ErrorDescription = "Invalid JWT Signature.",
            });

        var result = await Driver(new ThrowingCredentials(refused)).VerifyAsync(VertexEndpoint());

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Equal(AiVerificationFailureCategory.ProviderRejected, result.FailureCategory);
        Assert.Contains("Invalid JWT Signature.", result.Summary, StringComparison.Ordinal);
    }

    // A Vertex call mints a token before it reaches the model, so a runtime failure can come from the token
    // service. The shared classifier does not know that exception type and read every one of them as permanent,
    // so a token service that was briefly unavailable took the call out with it and nothing retried.
    [Fact]
    public void ATokenServiceOutageIsTransient()
    {
        var verdict = Driver(new ThrowingCredentials(new Exception("unused"))).ClassifyRuntimeFailure(
            new TokenResponseException(new TokenErrorResponse { Error = "temporarily_unavailable" }));

        Assert.True(verdict.IsTransient);
        Assert.Contains("token service", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACredentialTheTokenServiceRejectsIsPermanent()
    {
        var verdict = Driver(new ThrowingCredentials(new Exception("unused"))).ClassifyRuntimeFailure(
            new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant", ErrorDescription = "Invalid JWT Signature." }));

        Assert.False(verdict.IsTransient);
        Assert.Contains("Invalid JWT Signature.", verdict.Reason, StringComparison.Ordinal);
    }

    // Everything the token service did not raise still goes to the shared classifier.
    [Fact]
    public void AProviderRateLimitIsStillReadAsThrottling()
    {
        var verdict = Driver(new ThrowingCredentials(new Exception("unused"))).ClassifyRuntimeFailure(
            new HttpRequestException("HTTP 429", null, System.Net.HttpStatusCode.TooManyRequests));

        Assert.True(verdict.IsThrottled);
        Assert.True(verdict.IsTransient);
    }

    // Egress that blocks Google's token endpoint is a network problem, not a wrong credential, and it reaches
    // the operator under the status an unreachable host gets everywhere else in this driver.
    [Fact]
    public async Task ATokenEndpointThatCannotBeReachedIsAFailedVerification()
    {
        var unreachable = new HttpRequestException("Connection refused (oauth2.googleapis.com:443)");

        var result = await Driver(new ThrowingCredentials(unreachable)).VerifyAsync(VertexEndpoint());

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Equal("503", result.DriverMetadata?["httpStatus"]);
        Assert.Contains("Connection refused", result.Summary, StringComparison.Ordinal);
    }

    // Cancellation is not a verification outcome. Swallowing it would report a cancelled probe as a broken
    // credential and leave the caller waiting for a result it no longer wants.
    [Fact]
    public async Task ACancelledVertexProbeStaysCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Driver(new ThrowingCredentials(new OperationCanceledException(cancelled.Token)))
            .VerifyAsync(VertexEndpoint(), cancelled.Token));
    }

    [Fact]
    public void CachingIsClaimedBecauseGeminiDoesItWithoutBeingAsked()
    {
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "gemini-3-pro", [ProviderDeclaredProtocolModes.Auto]);

        Assert.True(Driver().GetChatRuntimeCapabilities(GeminiEndpoint(), model, ProviderDeclaredProtocolModes.Auto).SupportsPromptCaching);
    }

    // The transport rides on the endpoint, because the driver is constructed once for the family and every
    // connection of it is reached through the client factory the host attaches to that connection.
    private static ProviderEndpoint GeminiEndpoint(HttpMessageHandler? wire = null)
    {
        return new ProviderEndpoint(
            "meisterdev/googleVertex",
            "https://generativelanguage.googleapis.com",
            GoogleVertexProviderDriver.ApiKeyAuth,
            "gemini-key")
        {
            HostContext = wire is null ? null : new FakeProviderHostContext(wire),
        };
    }

    private static ProviderEndpoint VertexEndpoint()
    {
        return new ProviderEndpoint(
            "meisterdev/googleVertex",
            "https://europe-west4-aiplatform.googleapis.com",
            GoogleVertexProviderDriver.GcpAdcAuth,
            "{}")
        {
            DefaultQueryParams = new Dictionary<string, string> { ["project"] = "meister-dev-prod" },
        };
    }

    private static GoogleVertexProviderDriver Driver(IGoogleCredentialSource? credentials = null)
    {
        return new GoogleVertexProviderDriver(credentials ?? new StubCredentials());
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

    /// <summary>An endpoint nothing answers on, as the transport reports one.</summary>
    private sealed class UnreachableGoogleApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused (generativelanguage.googleapis.com:443)");
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

    /// <summary>A credential source whose token exchange fails the way the given exception describes.</summary>
    private sealed class ThrowingCredentials(Exception failure) : IGoogleCredentialSource
    {
        public Task AuthenticateAsync(
            HttpRequestMessage request,
            ProviderEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException(failure);
        }
    }
}
