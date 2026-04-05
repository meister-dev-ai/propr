// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Azure.Core;
using MeisterProPR.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public class AdoTokenValidatorTests
{
    private static AdoTokenValidator CreateValidator(HttpStatusCode statusCode, TokenCredential? credential = null)
    {
        var handler = new FakeHttpMessageHandler(statusCode);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return new AdoTokenValidator(factory, credential ?? Substitute.For<TokenCredential>(), NullLogger<AdoTokenValidator>.Instance);
    }

    [Fact]
    public async Task IsValidAsync_200Response_ReturnsTrue()
    {
        var validator = CreateValidator(HttpStatusCode.OK);
        Assert.True(await validator.IsValidAsync("valid-pat-token"));
    }

    [Fact]
    public async Task IsValidAsync_401Response_ReturnsFalse()
    {
        var validator = CreateValidator(HttpStatusCode.Unauthorized);
        Assert.False(await validator.IsValidAsync("invalid-pat-token"));
    }

    [Fact]
    public async Task IsValidAsync_403Response_ReturnsFalse()
    {
        var validator = CreateValidator(HttpStatusCode.Forbidden);
        Assert.False(await validator.IsValidAsync("bad-pat-token"));
    }

    [Fact]
    public async Task IsValidAsync_HttpRequestException_Propagates()
    {
        var handler = new FakeHttpMessageHandler(null);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var validator = new AdoTokenValidator(factory, Substitute.For<TokenCredential>(), NullLogger<AdoTokenValidator>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            validator.IsValidAsync("token"));
    }

    [Fact]
    public async Task IsValidAsync_JwtTokenWithOrgUrl_UsesIdentityEndpointWithServerCredentials()
    {
        string? capturedAuthScheme = null;
        Uri? capturedUri = null;
        var handler = new CapturingHttpMessageHandler(req =>
        {
            capturedUri = req.RequestUri;
            capturedAuthScheme = req.Headers.Authorization?.Scheme;
            // Return a valid identity response with count=1
            var content = new StringContent("{\"count\":1,\"value\":[{}]}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);

        var credential = Substitute.For<TokenCredential>();
        credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("server-token", DateTimeOffset.MaxValue));

        var validator = new AdoTokenValidator(factory, credential, NullLogger<AdoTokenValidator>.Instance);

        // JWT with nameid claim (base64url of {"nameid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"})
        var result = await validator.IsValidAsync(
            "eyJhbGciOiJSUzI1NiJ9.eyJuYW1laWQiOiJhYWFhYWFhYS1iYmJiLWNjY2MtZGRkZC1lZWVlZWVlZWVlZWUifQ.sig",
            "https://dev.azure.com/myorg");

        Assert.True(result);
        Assert.Equal("Bearer", capturedAuthScheme);
        Assert.NotNull(capturedUri);
        Assert.Contains("vssps.dev.azure.com", capturedUri!.Host);
        Assert.Contains("identities", capturedUri.AbsolutePath);
    }

    [Fact]
    public async Task IsValidAsync_PatToken_SendsBasicAuth()
    {
        string? capturedAuthHeader = null;
        var handler = new CapturingHttpMessageHandler(req =>
        {
            capturedAuthHeader = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var validator = new AdoTokenValidator(factory, Substitute.For<TokenCredential>(), NullLogger<AdoTokenValidator>.Instance);

        await validator.IsValidAsync("my-pat-token");

        Assert.NotNull(capturedAuthHeader);
        Assert.StartsWith("Basic ", capturedAuthHeader!);
    }

    [Fact]
    public async Task IsValidAsync_PatToken_UsesConnectionDataEndpoint()
    {
        Uri? capturedUri = null;
        var handler = new CapturingHttpMessageHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        var validator = new AdoTokenValidator(factory, Substitute.For<TokenCredential>(), NullLogger<AdoTokenValidator>.Instance);

        await validator.IsValidAsync("my-pat-token");

        Assert.NotNull(capturedUri);
        Assert.Contains("app.vssps.visualstudio.com", capturedUri!.Host);
        Assert.Contains("connectionData", capturedUri.AbsolutePath);
    }

    private sealed class FakeHttpMessageHandler(HttpStatusCode? statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (statusCode is null)
            {
                throw new HttpRequestException("Simulated network error");
            }

            return Task.FromResult(new HttpResponseMessage(statusCode.Value));
        }
    }

    private sealed class CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
