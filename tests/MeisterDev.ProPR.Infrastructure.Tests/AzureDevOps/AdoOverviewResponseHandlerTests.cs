// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using Azure.Core;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using NSubstitute;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoOverviewResponseHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PreliminaryDenialHasConnectionScopeAndFixedText(HttpStatusCode status)
    {
        using var client = new HttpClient(new AdoOverviewResponseHandler { InnerHandler = new ResponseHandler(status) });
        var denied = await Assert.ThrowsAsync<ProviderAccessDeniedException>(() => client.GetAsync("https://dev.azure.com/acme/_apis/connectionData"));
        Assert.Equal(status, denied.StatusCode);
        Assert.True(denied.ConnectionWide);
        Assert.DoesNotContain("Private detail", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SdkAuthenticationChallengeOrPreauthenticationCompletesBeforeOutcomeClassification()
    {
        var transport = new AuthenticatedResponseHandler();
        var credentials = new VssCredentials(new VssBasicCredential("fixture", "fixture-token"))
        {
            PromptType = CredentialPromptType.DoNotPrompt,
        };
        var sdk = new VssHttpMessageHandler(credentials, VssClientHttpRequestSettings.Default.Clone(), transport);
        using var client = new HttpClient(new AdoOverviewResponseHandler { InnerHandler = sdk });
        using var response = await client.GetAsync("https://dev.azure.com/acme/_apis/connectionData");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(transport.Authenticated);
        Assert.InRange(transport.Requests, 1, 3);
    }

    [Fact]
    public async Task OverviewConnectionsAreSeparateFromOrdinaryCachedConnections()
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>());
        var credentials = new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, "fixture-token");
        var ordinary = await factory.GetConnectionAsync("https://dev.azure.com/acme", credentials);
        var overview = await factory.GetOverviewConnectionAsync("https://dev.azure.com/acme", credentials, default);
        Assert.NotSame(ordinary, overview);
        Assert.Same(ordinary, await factory.GetConnectionAsync("https://dev.azure.com/acme", credentials));
        Assert.Same(overview, await factory.GetOverviewConnectionAsync("https://dev.azure.com/acme", credentials, default));
    }

    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(
                new HttpResponseMessage(status)
                {
                    RequestMessage = request, Content = JsonContent.Create(new { message = "Private detail" }),
                });
    }

    private sealed class AuthenticatedResponseHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Authenticated { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            this.Requests++;
            this.Authenticated = request.Headers.Authorization is not null;
            var response = new HttpResponseMessage(this.Authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                RequestMessage = request, Content = JsonContent.Create(Array.Empty<object>()),
            };
            if (!this.Authenticated)
            {
                response.Headers.WwwAuthenticate.Add(new("Basic", "realm=\"fixture\""));
            }

            return Task.FromResult(response);
        }
    }
}
