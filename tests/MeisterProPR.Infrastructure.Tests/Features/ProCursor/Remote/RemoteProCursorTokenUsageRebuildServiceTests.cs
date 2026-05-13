// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.Features.ProCursor.Remote;

public sealed class RemoteProCursorTokenUsageRebuildServiceTests
{
    [Fact]
    public async Task RebuildAsync_MapsRouteAndPayload()
    {
        var clientId = Guid.NewGuid();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    $"http://procursor.internal/internal/procursor/clients/{clientId:D}/token-usage/rebuild",
                    request.RequestUri!.ToString());

                var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                Assert.Equal("2026-04-01", payload.GetProperty("from").GetString());
                Assert.Equal("2026-04-30", payload.GetProperty("to").GetString());
                Assert.True(payload.GetProperty("includeMonthly").GetBoolean());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new ProCursorTokenUsageRebuildResponse(
                            new DateOnly(2026, 4, 1),
                            new DateOnly(2026, 4, 30),
                            6,
                            new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero))),
                };
            }))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var service = new RemoteProCursorTokenUsageRebuildService(
            httpClient,
            NullLogger<RemoteProCursorTokenUsageRebuildService>.Instance);

        var result = await service.RebuildAsync(
            clientId,
            new ProCursorTokenUsageRebuildRequest(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)),
            CancellationToken.None);

        Assert.Equal(6, result.RecomputedBucketCount);
    }

    [Fact]
    public async Task RebuildAsync_WhenUnauthorized_ThrowsDependencyUnavailable()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))))
        {
            BaseAddress = new Uri("http://procursor.internal/"),
        };

        var service = new RemoteProCursorTokenUsageRebuildService(
            httpClient,
            NullLogger<RemoteProCursorTokenUsageRebuildService>.Instance);

        var ex = await Assert.ThrowsAsync<ProCursorDependencyUnavailableException>(() =>
            service.RebuildAsync(
                Guid.NewGuid(),
                new ProCursorTokenUsageRebuildRequest(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)),
                CancellationToken.None));

        Assert.Contains("shared access credential", ex.Message, StringComparison.OrdinalIgnoreCase);
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
