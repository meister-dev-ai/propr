// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     How a stored Google credential is turned into request authentication.
/// </summary>
/// <remarks>
///     Only the refusals are exercised here. A credential that builds goes on to exchange itself for a token
///     against Google's own endpoint, which a unit test has no business reaching, so the assertions stop at the
///     point where the stored secret is judged. That is also where the operator-facing message is decided, which
///     is the part worth pinning: the library's own errors name a JSON field, not what to store instead.
/// </remarks>
public sealed class GoogleCredentialSourceTests
{
    private const string VertexBaseUrl = "https://europe-west4-aiplatform.googleapis.com";
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com";

    [Fact]
    public async Task TheGeminiApiTakesTheStoredKeyAsAHeaderAndBuildsNoCredential()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GeminiBaseUrl);

        await new GoogleCredentialSource().AuthenticateAsync(request, Endpoint(GeminiBaseUrl, "a-gemini-api-key"));

        Assert.Equal(
            "a-gemini-api-key",
            Assert.Single(request.Headers.GetValues(GoogleCredentialSource.ApiKeyHeaderName)));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task AMissingCredentialSaysWhichKindEachSurfaceWants()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, VertexBaseUrl);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GoogleCredentialSource().AuthenticateAsync(request, Endpoint(VertexBaseUrl, secret: null)));

        Assert.Contains("API key for the Gemini API", failure.Message, StringComparison.Ordinal);
        Assert.Contains("service account for Vertex AI", failure.Message, StringComparison.Ordinal);
    }

    // The likely mistake is pasting a Gemini API key into a Vertex profile. The library would report a JSON parse
    // failure, which does not tell the operator that this surface wants something else entirely.
    [Fact]
    public async Task ACredentialThatIsNotJsonAtAllIsRefusedInTermsOfWhatToStore()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, VertexBaseUrl);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GoogleCredentialSource().AuthenticateAsync(request, Endpoint(VertexBaseUrl, "not-json-at-all")));

        Assert.Contains("JSON key of a service account", failure.Message, StringComparison.Ordinal);
    }

    // The credential kind is read from the payload rather than assumed, so a payload naming a kind Google does not
    // build must still come back as the actionable message and not as whatever the factory threw.
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"type\":\"service_account\"}")]
    [InlineData("{\"type\":\"external_account\"}")]
    [InlineData("{\"type\":\"authorized_user\"}")]
    [InlineData("{\"type\":\"a_kind_that_does_not_exist\"}")]
    [InlineData("{\"type\":42}")]
    [InlineData("[]")]
    public async Task AnIncompleteOrUnknownCredentialKindStillReachesTheOperatorAsAdvice(string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, VertexBaseUrl);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GoogleCredentialSource().AuthenticateAsync(request, Endpoint(VertexBaseUrl, secret)));

        Assert.Contains("JSON key of a service account", failure.Message, StringComparison.Ordinal);
    }

    // A refusal must not quote the credential back: these messages reach logs and the management UI.
    [Fact]
    public async Task ARefusalDoesNotEchoTheStoredSecret()
    {
        const string secret = "{\"type\":\"service_account\",\"private_key\":\"super-secret-material\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, VertexBaseUrl);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GoogleCredentialSource().AuthenticateAsync(request, Endpoint(VertexBaseUrl, secret)));

        Assert.DoesNotContain("super-secret-material", failure.ToString(), StringComparison.Ordinal);
    }

    private static ProviderEndpoint Endpoint(string baseUrl, string? secret)
    {
        return new ProviderEndpoint(AiProviderKind.GoogleVertex, baseUrl, AiAuthMode.GcpAdc, secret);
    }
}
