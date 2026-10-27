// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.Auth;

/// <summary>
///     The HTTP client that the identity module registers for requests to identity providers, used for a token
///     request as both sign-in flows send it.
/// </summary>
public sealed class ExternalSignInClientTests
{
    [Fact]
    public async Task OutsideDevelopment_ATokenRequestOverPlainHttpIsRefusedBeforeItIsSent()
    {
        var provider = new StubProvider(_ => Json("""{"id_token":"token"}"""));
        using var services = BuildServices(Environments.Production, provider);

        var (statusCode, document) = await RedeemAsync(services, "http://id.example.test/token");

        Assert.Null(statusCode);
        Assert.Null(document);
        Assert.Equal(0, provider.Requests);
    }

    [Fact]
    public async Task InDevelopment_ATokenRequestOverPlainHttpIsSent()
    {
        var provider = new StubProvider(_ => Json("""{"id_token":"token"}"""));
        using var services = BuildServices(Environments.Development, provider);

        var (statusCode, document) = await RedeemAsync(services, "http://localhost:8080/token");

        using (document)
        {
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.Equal("token", document?.RootElement.GetProperty("id_token").GetString());
        }

        Assert.Equal(1, provider.Requests);
    }

    [Fact]
    public async Task ATokenResponseAboveTheLimitIsRefused()
    {
        var oversized = $$"""{"id_token":"{{new string('a', AuthorizationCodeFlow.MaxResponseContentBytes)}}"}""";
        var provider = new StubProvider(_ => Json(oversized));
        using var services = BuildServices(Environments.Production, provider);

        var (statusCode, document) = await RedeemAsync(services, "https://id.example.test/token");

        Assert.Null(statusCode);
        Assert.Null(document);
        Assert.Equal(1, provider.Requests);
    }

    [Fact]
    public async Task ATokenResponseWithinTheLimitIsRead()
    {
        var token = new string('a', 64 * 1024);
        var provider = new StubProvider(_ => Json($$"""{"id_token":"{{token}}"}"""));
        using var services = BuildServices(Environments.Production, provider);

        var (_, document) = await RedeemAsync(services, "https://id.example.test/token");

        using (document)
        {
            Assert.Equal(token, document?.RootElement.GetProperty("id_token").GetString());
        }
    }

    private static Task<(HttpStatusCode? StatusCode, System.Text.Json.JsonDocument? Document)> RedeemAsync(
        ServiceProvider services,
        string tokenEndpoint)
    {
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(AuthorizationCodeFlow.HttpClientName);
        return AuthorizationCodeFlow.RedeemCodeAsync(
            client,
            new Uri(tokenEndpoint),
            [new KeyValuePair<string, string>("code", "code-1"), new KeyValuePair<string, string>("client_secret", "secret")],
            null,
            NullLogger.Instance,
            CancellationToken.None);
    }

    private static ServiceProvider BuildServices(string environmentName, StubProvider provider)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityAndAccessModule(new ConfigurationBuilder().Build(), environment);
        services.AddHttpClient(AuthorizationCodeFlow.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => provider);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>The identity provider at the end of the client, which counts the requests that reach it.</summary>
    private sealed class StubProvider(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests++;
            return Task.FromResult(respond(request));
        }
    }
}
