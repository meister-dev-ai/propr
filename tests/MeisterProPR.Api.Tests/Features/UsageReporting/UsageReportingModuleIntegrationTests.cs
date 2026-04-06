// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Api.Tests.Controllers;
using MeisterProPR.Api.Tests.Controllers.ProCursor;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Features.UsageReporting;

public sealed class ClientTokenUsageModuleIntegrationTests(ClientTokenUsageControllerTests.TokenUsageApiFactory factory)
    : IClassFixture<ClientTokenUsageControllerTests.TokenUsageApiFactory>
{
    [Fact]
    public async Task GetClientUsage_WhenSamplesExist_ReturnsAggregatedTotals()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ClientTokenUsageSamples.RemoveRange(db.ClientTokenUsageSamples);
            db.ClientTokenUsageSamples.AddRange(
                new ClientTokenUsageSample
                {
                    Id = Guid.NewGuid(),
                    ClientId = factory.ClientId,
                    ModelId = "gpt-4o",
                    Date = new DateOnly(2026, 4, 4),
                    InputTokens = 100,
                    OutputTokens = 25,
                },
                new ClientTokenUsageSample
                {
                    Id = Guid.NewGuid(),
                    ClientId = factory.ClientId,
                    ModelId = "gpt-4o-mini",
                    Date = new DateOnly(2026, 4, 4),
                    InputTokens = 40,
                    OutputTokens = 10,
                });
            await db.SaveChangesAsync();
        }

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/token-usage?from=2026-04-04&to=2026-04-04");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(140, body.GetProperty("totalInputTokens").GetInt64());
        Assert.Equal(35, body.GetProperty("totalOutputTokens").GetInt64());
        Assert.Equal(2, body.GetProperty("samples").GetArrayLength());
    }
}

public sealed class ProCursorTokenUsageModuleIntegrationTests(ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory factory)
    : IClassFixture<ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetProCursorUsage_WhenEventsExist_ReturnsAggregatedTotals()
    {
        var sourceId = await factory.SeedSourceAsync(displayName: "Platform Wiki", repositoryId: "wiki-a", defaultBranch: "main");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ProCursorTokenUsageEvents.AddRange(
                CreateEvent(factory.ClientId, sourceId, new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero), 120, 0, 0.00012m),
                CreateEvent(factory.ClientId, sourceId, new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero), 80, 0, 0.00008m));
            await db.SaveChangesAsync();
        }

        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/procursor/token-usage?from=2026-04-04&to=2026-04-04&granularity=daily&groupBy=source");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientAdministratorToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(200, body.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        Assert.Single(body.GetProperty("series").EnumerateArray());
        Assert.Single(body.GetProperty("topSources").EnumerateArray());
    }

    private static ProCursorTokenUsageEvent CreateEvent(
        Guid clientId,
        Guid sourceId,
        DateTimeOffset occurredAtUtc,
        long promptTokens,
        long completionTokens,
        decimal estimatedCostUsd)
    {
        return new ProCursorTokenUsageEvent(
            Guid.NewGuid(),
            clientId,
            sourceId,
            "Platform Wiki",
            $"pcidx:test:{occurredAtUtc:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}",
            occurredAtUtc,
            ProCursorTokenUsageCallType.Embedding,
            "text-embedding-3-small",
            "text-embedding-3-small",
            "cl100k_base",
            promptTokens,
            completionTokens,
            false,
            estimatedCostUsd,
            true);
    }
}
