// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Net;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class ExtensionAuthorizationRedirectTests
{
    private const string Callback = "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr";

    [Fact]
    public async Task ATrustedSeparateHttpsFrontendReceivesTheConsentPage()
    {
        var auth = Substitute.For<IExtensionAuthorizationService>();
        auth.StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ExtensionAuthorizationStart(PendingRequest: "protected-request"));
        using var server = Server(auth, new CapturedLogs());
        using var client = server.CreateClient();
        using var response = await client.GetAsync(AuthorizeUrl("https://ui.test"));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://ui.test/extension/authorize?request=protected-request", response.Headers.Location!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://untrusted.test")]
    [InlineData("http://ui.test")]
    [InlineData("https://user:password@ui.test")]
    [InlineData("https://ui.test?next=other")]
    [InlineData("https://ui.test#fragment")]
    [InlineData("https://ui.test/another-path")]
    [InlineData("")]
    [InlineData("https://ui.test ")]
    public async Task InvalidUiOriginsAreRejectedBeforeStartingAuthorization(string origin)
    {
        var auth = Substitute.For<IExtensionAuthorizationService>();
        auth.StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ExtensionAuthorizationStart(PendingRequest: "protected-request"));
        using var server = Server(auth, new CapturedLogs());
        using var client = server.CreateClient();
        using var response = await client.GetAsync(AuthorizeUrl(origin));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        await auth.DidNotReceive().StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleUiOriginsAreRejectedBeforeStartingAuthorization()
    {
        var auth = Substitute.For<IExtensionAuthorizationService>();
        using var server = Server(auth, new CapturedLogs());
        using var client = server.CreateClient();
        using var response = await client.GetAsync(AuthorizeUrl("https://ui.test") + "&ui_origin=https%3A%2F%2Fapi.test");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await auth.DidNotReceive().StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OmittingTheOriginKeepsTheSameOriginConsentPage()
    {
        var auth = Substitute.For<IExtensionAuthorizationService>();
        auth.StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ExtensionAuthorizationStart(PendingRequest: "protected-request"));
        using var server = Server(auth, new CapturedLogs());
        using var client = server.CreateClient();
        using var response = await client.GetAsync(AuthorizeUrl());
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://api.test/extension/authorize?request=protected-request", response.Headers.Location!.AbsoluteUri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedirectResponsesPreserveLocationWithoutLoggingCodesOrProtectedRequests(bool nativeCallback)
    {
        const string secret = "one-time-secret-must-not-be-logged";
        var auth = Substitute.For<IExtensionAuthorizationService>();
        var expected = nativeCallback ? Callback + "?code=" + secret : "https://api.test/extension/authorize?request=" + secret;
        auth.StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(nativeCallback ? new ExtensionAuthorizationStart(RedirectUrl: expected) : new ExtensionAuthorizationStart(PendingRequest: secret));
        var logs = new CapturedLogs();
        using var server = Server(auth, logs);
        using var client = server.CreateClient();
        using var response = await client.GetAsync(AuthorizeUrl());
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.AbsoluteUri);
        Assert.DoesNotContain(secret, string.Join('\n', logs.Messages), StringComparison.Ordinal);
    }

    private static string AuthorizeUrl(string? origin = null) => "https://api.test/auth/extension/authorize?client_id=propr-lens&redirect_uri="
                                                                 + Uri.EscapeDataString(Callback) + "&response_type=code&code_challenge=" + new string('a', 43)
                                                                 + "&code_challenge_method=S256&state=opaque-state" +
                                                                 (origin is null ? "" : "&ui_origin=" + Uri.EscapeDataString(origin));

    private static TestServer Server(IExtensionAuthorizationService auth, CapturedLogs logs) => new(
        new WebHostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MEISTER_PUBLIC_BASE_URL"] = "https://api.test/api",
                    ["CORS_ORIGINS"] = "https://ui.test,http://ui.test",
                }))
            .ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Information).AddProvider(logs))
            .ConfigureServices(services =>
            {
                services.AddSingleton(new ExtensionAuthorizationOptions("https://api.test/api", [Callback]));
                services.AddSingleton(auth);
                services.AddControllers().AddApplicationPart(typeof(ExtensionAuthorizationController).Assembly);
            })
            .Configure(app => app.UseRouting().UseEndpoints(endpoints => endpoints.MapControllers())));

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this.Messages);

        public void Dispose()
        {
        }

        private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }
}
