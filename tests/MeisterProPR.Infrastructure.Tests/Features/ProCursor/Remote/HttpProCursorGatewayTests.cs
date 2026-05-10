// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;

namespace MeisterProPR.Infrastructure.Tests.Features.ProCursor.Remote;

public sealed class HttpProCursorGatewayTests
{
    [Fact]
    public async Task ListSourcesAsync_MapsRouteAndPayload()
    {
        var clientId = Guid.NewGuid();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"http://procursor.internal/internal/procursor/clients/{clientId:D}/sources", request.RequestUri!.ToString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create<IReadOnlyList<ProCursorKnowledgeSourceDto>>([]),
            };
        }))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var gateway = new HttpProCursorGateway(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpProCursorGateway>.Instance);
        var result = await gateway.ListSourcesAsync(clientId, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AskKnowledgeAsync_WhenUpstreamIsUnauthorized_ThrowsDependencyUnavailable()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var gateway = new HttpProCursorGateway(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpProCursorGateway>.Instance);

        var ex = await Assert.ThrowsAsync<ProCursorDependencyUnavailableException>(() =>
            gateway.AskKnowledgeAsync(new ProCursorKnowledgeQueryRequest(Guid.NewGuid(), "where", null, null), CancellationToken.None));

        Assert.Contains("shared access credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueueRefreshAsync_WhenUpstreamReturnsConflict_ThrowsInvalidOperation()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("refresh conflict"),
        })))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var gateway = new HttpProCursorGateway(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpProCursorGateway>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.QueueRefreshAsync(Guid.NewGuid(), Guid.NewGuid(), new ProCursorRefreshRequest(), CancellationToken.None));

        Assert.Equal("refresh conflict", ex.Message);
    }

    [Fact]
    public async Task GetSymbolInsightAsync_WhenHttpClientFails_ThrowsDependencyUnavailable()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new HttpRequestException("boom")))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var gateway = new HttpProCursorGateway(httpClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpProCursorGateway>.Instance);

        await Assert.ThrowsAsync<ProCursorDependencyUnavailableException>(() =>
            gateway.GetSymbolInsightAsync(
                new ProCursorSymbolQueryRequest(Guid.NewGuid(), "Greeter", "qualifiedName", null, "indexedSnapshot", null, 10),
                CancellationToken.None));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}
