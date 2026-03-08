using System.Net;
using MeisterProPR.Infrastructure.AzureDevOps;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public class AdoTokenValidatorTests
{
    private static IHttpClientFactory CreateFactory(HttpStatusCode statusCode)
    {
        var handler = new FakeHttpMessageHandler(statusCode);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("AdoTokenValidator").Returns(client);
        return factory;
    }

    [Fact]
    public async Task IsValidAsync_200Response_ReturnsTrue()
    {
        var factory = CreateFactory(HttpStatusCode.OK);
        var validator = new AdoTokenValidator(factory);

        var result = await validator.IsValidAsync("valid-token");

        Assert.True(result);
    }

    [Fact]
    public async Task IsValidAsync_401Response_ReturnsFalse()
    {
        var factory = CreateFactory(HttpStatusCode.Unauthorized);
        var validator = new AdoTokenValidator(factory);

        var result = await validator.IsValidAsync("invalid-token");

        Assert.False(result);
    }

    [Fact]
    public async Task IsValidAsync_403Response_ReturnsFalse()
    {
        var factory = CreateFactory(HttpStatusCode.Forbidden);
        var validator = new AdoTokenValidator(factory);

        var result = await validator.IsValidAsync("bad-token");

        Assert.False(result);
    }

    [Fact]
    public async Task IsValidAsync_HttpRequestException_Propagates()
    {
        var handler = new FakeHttpMessageHandler(null);
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("AdoTokenValidator").Returns(client);
        var validator = new AdoTokenValidator(factory);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            validator.IsValidAsync("token"));
    }

    [Fact]
    public async Task IsValidAsync_SendsBearerToken()
    {
        string? capturedAuthHeader = null;
        var handler = new CapturingHttpMessageHandler(req =>
        {
            capturedAuthHeader = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("AdoTokenValidator").Returns(client);
        var validator = new AdoTokenValidator(factory);

        await validator.IsValidAsync("my-secret-token");

        Assert.NotNull(capturedAuthHeader);
        Assert.Contains("Bearer", capturedAuthHeader!);
        Assert.Contains("my-secret-token", capturedAuthHeader);
    }

    [Fact]
    public async Task IsValidAsync_UsesCorrectEndpoint()
    {
        Uri? capturedUri = null;
        var handler = new CapturingHttpMessageHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("AdoTokenValidator").Returns(client);
        var validator = new AdoTokenValidator(factory);

        await validator.IsValidAsync("my-token");

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