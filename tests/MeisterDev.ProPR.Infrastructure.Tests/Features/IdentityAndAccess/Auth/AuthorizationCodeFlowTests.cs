// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.ProPR.Infrastructure.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.Auth;

public sealed class AuthorizationCodeFlowTests
{
    private static readonly Uri TokenEndpoint = new("https://id.example.test/token");

    /// <summary>The example of RFC 7636, appendix B.</summary>
    [Fact]
    public void ComputeCodeChallenge_ReturnsTheS256ChallengeOfTheVerifier()
    {
        var challenge = AuthorizationCodeFlow.ComputeCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void CreateRandomValue_ReturnsAValueThatQualifiesAsCodeVerifier()
    {
        var first = AuthorizationCodeFlow.CreateRandomValue();
        var second = AuthorizationCodeFlow.CreateRandomValue();

        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildAuthorizationUri_AppendsTheEscapedParametersToTheQueryOfTheEndpoint()
    {
        var uri = AuthorizationCodeFlow.BuildAuthorizationUri(
            new Uri("https://id.example.test/authorize?prompt=login"),
            [new("redirect_uri", "https://propr.example.test/auth/callback"), new("scope", "openid email")]);

        Assert.Equal(
            "https://id.example.test/authorize?prompt=login&redirect_uri=https%3A%2F%2Fpropr.example.test%2Fauth%2Fcallback&scope=openid%20email",
            uri.AbsoluteUri);
    }

    /// <summary>
    ///     The code holds characters that form encoding changes, so a body built without the encoding differs from the
    ///     expected one.
    /// </summary>
    [Fact]
    public async Task RedeemCodeAsync_PostsTheFormAndReturnsTheJsonAnswer()
    {
        HttpRequestMessage? sent = null;
        string? sentBody = null;
        string? sentContentType = null;
        using var client = new HttpClient(
            new StubHandler(async request =>
            {
                sent = request;
                sentBody = await request.Content!.ReadAsStringAsync();
                sentContentType = request.Content.Headers.ContentType?.MediaType;
                return Json(HttpStatusCode.OK, """{"id_token":"token"}""");
            }));

        var (statusCode, document) = await AuthorizationCodeFlow.RedeemCodeAsync(
            client,
            TokenEndpoint,
            [new("code", "a+b&c=d/e f"), new("code_verifier", "verifier-1")],
            "agent/1.0",
            NullLogger.Instance,
            CancellationToken.None);

        using (document)
        {
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.Equal("token", document?.RootElement.GetProperty("id_token").GetString());
        }

        Assert.Equal(HttpMethod.Post, sent?.Method);
        Assert.Equal(TokenEndpoint, sent?.RequestUri);
        Assert.Equal("code=a%2Bb%26c%3Dd%2Fe+f&code_verifier=verifier-1", sentBody);
        Assert.Equal("application/x-www-form-urlencoded", sentContentType);
        Assert.Contains("agent/1.0", sent?.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeemCodeAsync_WhenTheEndpointRefusesTheCode_ReturnsTheStatusWithoutDocument()
    {
        using var client = new HttpClient(
            new StubHandler(_ =>
                Task.FromResult(Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""))));

        var (statusCode, document) = await AuthorizationCodeFlow.RedeemCodeAsync(
            client,
            TokenEndpoint,
            [new("code", "code-1")],
            null,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, statusCode);
        Assert.Null(document);
    }

    [Fact]
    public async Task RedeemCodeAsync_WhenTheAnswerIsNotJson_ReturnsNoDocument()
    {
        using var client = new HttpClient(
            new StubHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") })));

        var (statusCode, document) = await AuthorizationCodeFlow.RedeemCodeAsync(
            client,
            TokenEndpoint,
            [new("code", "code-1")],
            null,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Null(document);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}
