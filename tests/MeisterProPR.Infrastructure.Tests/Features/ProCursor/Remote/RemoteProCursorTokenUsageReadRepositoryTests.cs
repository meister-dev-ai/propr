// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;

namespace MeisterProPR.Infrastructure.Tests.Features.ProCursor.Remote;

public sealed class RemoteProCursorTokenUsageReadRepositoryTests
{
    [Fact]
    public async Task GetClientUsageAsync_MapsRouteAndQuery()
    {
        var clientId = Guid.NewGuid();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"http://procursor.internal/internal/procursor/clients/{clientId:D}/token-usage?from=2026-04-01&to=2026-04-30&granularity=daily&groupBy=source",
                request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    new ProCursorTokenUsageResponse(
                        clientId,
                        new DateOnly(2026, 4, 1),
                        new DateOnly(2026, 4, 30),
                        ProCursorTokenUsageGranularity.Daily,
                        "source",
                        new ProCursorTokenUsageTotalsDto(120, 0, 120, 0.00012m, 1, 0),
                        [],
                        [])),
            });
        }))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var repository = new RemoteProCursorTokenUsageReadRepository(
            httpClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteProCursorTokenUsageReadRepository>.Instance);

        var result = await repository.GetClientUsageAsync(
            clientId,
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 4, 30),
            ProCursorTokenUsageGranularity.Daily,
            "source",
            CancellationToken.None);

        Assert.Equal(120, result.Totals.TotalTokens);
    }

    [Fact]
    public async Task GetRecentEventsAsync_WhenUnauthorized_ThrowsDependencyUnavailable()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var repository = new RemoteProCursorTokenUsageReadRepository(
            httpClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteProCursorTokenUsageReadRepository>.Instance);

        var ex = await Assert.ThrowsAsync<ProCursorDependencyUnavailableException>(() =>
            repository.GetRecentEventsAsync(Guid.NewGuid(), Guid.NewGuid(), 10, CancellationToken.None));

        Assert.Contains("shared access credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSourceUsageAsync_WhenMissing_ReturnsNull()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var repository = new RemoteProCursorTokenUsageReadRepository(
            httpClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteProCursorTokenUsageReadRepository>.Instance);

        var result = await repository.GetSourceUsageAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 4, 30),
            ProCursorTokenUsageGranularity.Monthly,
            CancellationToken.None);

        Assert.Null(result);
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
