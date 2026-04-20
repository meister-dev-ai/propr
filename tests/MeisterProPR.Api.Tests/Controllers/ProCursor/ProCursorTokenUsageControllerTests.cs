// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers.ProCursor;

/// <summary>
///     Integration tests for <see cref="MeisterProPR.Api.Controllers.ProCursorTokenUsageController" />.
/// </summary>
public sealed class ProCursorTokenUsageControllerTests(ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory factory)
    : IClassFixture<ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync()
    {
        return factory.ResetAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetClientUsage_ClientAdministrator_ReturnsAggregatedUsage()
    {
        var sourceId = await factory.SeedSourceAsync(
            "Platform Wiki",
            repositoryId: "wiki-a",
            defaultBranch: "wikiMain");
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
            120,
            0,
            0.00012m);
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
            80,
            0,
            0.00008m);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage?from=2026-04-04&to=2026-04-04&granularity=daily&groupBy=source");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.ClientId.ToString(), body.GetProperty("clientId").GetString());
        Assert.Equal(200, body.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        Assert.Equal(2, body.GetProperty("totals").GetProperty("eventCount").GetInt64());
        Assert.Single(body.GetProperty("series").EnumerateArray());
        Assert.Single(body.GetProperty("topSources").EnumerateArray());
        var breakdown = body.GetProperty("series")[0].GetProperty("breakdown");
        Assert.Single(breakdown.EnumerateArray());
        Assert.Equal("Platform Wiki", breakdown[0].GetProperty("sourceDisplayName").GetString());
    }

    [Fact]
    public async Task GetClientUsage_ClientUser_Returns403()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage?from=2026-04-04&to=2026-04-04");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetClientUsage_WithoutCredentials_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/admin/clients/{factory.ClientId}/procursor/token-usage?from=2026-04-04&to=2026-04-04");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetTopSources_ClientAdministrator_ReturnsRankedSources()
    {
        var dominantSourceId = await factory.SeedSourceAsync("Dominant Source", repositoryId: "repo-dominant");
        var otherSourceId = await factory.SeedSourceAsync("Smaller Source", repositoryId: "repo-smaller");
        var today = DateTimeOffset.UtcNow;

        await this.SeedUsageEventAsync(dominantSourceId, "Dominant Source", today, 300, 0, 0.0003m);
        await this.SeedUsageEventAsync(otherSourceId, "Smaller Source", today.AddHours(-1), 100, 0, 0.0001m);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/top-sources?period=30d&limit=2");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Dominant Source", items[0].GetProperty("sourceDisplayName").GetString());
        Assert.Equal(
            400,
            items[0].GetProperty("totalTokens").GetInt64() + items[1].GetProperty("totalTokens").GetInt64());
    }

    [Fact]
    public async Task GetTopSources_LimitAboveMaximum_Returns400()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/top-sources?period=30d&limit=1001");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_ClientAdministrator_ReturnsCsv()
    {
        var sourceId = await factory.SeedSourceAsync(
            "Platform Wiki",
            repositoryId: "wiki-export",
            defaultBranch: "wikiMain");
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 10, 0, 0, TimeSpan.Zero),
            90,
            0,
            0.00009m,
            "/docs/intro.md");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/export?from=2026-04-04&to=2026-04-04");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("date,sourceId,sourceDisplayName", csv, StringComparison.Ordinal);
        Assert.Contains("Platform Wiki", csv, StringComparison.Ordinal);
        Assert.Contains("/docs/intro.md", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSourceUsage_ClientAdministrator_ReturnsSourceAggregate()
    {
        var sourceId = await factory.SeedSourceAsync(
            "Platform Wiki",
            repositoryId: "wiki-source",
            defaultBranch: "wikiMain");
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
            120,
            0,
            0.00012m,
            modelName: "text-embedding-3-small");
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
            80,
            40,
            0.0002m,
            modelName: "gpt-4.1-mini");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/token-usage?period=30d&granularity=daily");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(sourceId.ToString(), body.GetProperty("sourceId").GetString());
        Assert.Equal("Platform Wiki", body.GetProperty("sourceDisplayName").GetString());
        Assert.Equal(240, body.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        Assert.Equal(2, body.GetProperty("byModel").GetArrayLength());
        Assert.Contains(
            "/token-usage/events",
            body.GetProperty("recentEventsHref").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSourceUsage_ClientUser_Returns403()
    {
        var sourceId = await factory.SeedSourceAsync(
            "Platform Wiki",
            repositoryId: "wiki-forbidden",
            defaultBranch: "wikiMain");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/token-usage?period=30d");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRecentEvents_ClientAdministrator_ReturnsSafeEvents()
    {
        var sourceId = await factory.SeedSourceAsync(
            "Platform Wiki",
            repositoryId: "wiki-events",
            defaultBranch: "wikiMain");
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
            120,
            0,
            0.00012m,
            "/docs/intro.md",
            resourceId: "ado://wiki/intro");
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
            60,
            10,
            0.00007m,
            "/docs/setup.md",
            resourceId: "ado://wiki/setup");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/token-usage/events?limit=10");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("ado://wiki/setup", items[0].GetProperty("resourceId").GetString());
        Assert.Equal("/docs/setup.md", items[0].GetProperty("sourcePath").GetString());
        Assert.Equal("ado://wiki/intro", items[1].GetProperty("resourceId").GetString());
    }

    [Fact]
    public async Task GetRecentEvents_ClientAdministrator_DoesNotExposeSafeMetadataJson()
    {
        var sourceId = await factory.SeedSourceAsync(
            "Platform Wiki",
            repositoryId: "wiki-safe-metadata",
            defaultBranch: "wikiMain");
        const string safeMetadataMarker = "private-safe-metadata-marker";
        await this.SeedUsageEventAsync(
            sourceId,
            "Platform Wiki",
            new DateTimeOffset(2026, 4, 4, 8, 0, 0, TimeSpan.Zero),
            120,
            0,
            0.00012m,
            safeMetadataJson: $"{{\"traceId\":\"{safeMetadataMarker}\"}}");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/token-usage/events?limit=10");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("safeMetadataJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(safeMetadataMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFreshness_ClientAdministrator_ReturnsRollupMetadata()
    {
        await this.SeedRollupAsync(new DateOnly(2026, 4, 1), new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/freshness");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.ClientId.ToString(), body.GetProperty("clientId").GetString());
        Assert.Equal("2026-04-05T12:00:00+00:00", body.GetProperty("lastRollupCompletedAtUtc").GetString());
    }

    [Fact]
    public async Task Rebuild_ClientAdministrator_ReturnsRebuildSummary()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var rebuildService = scope.ServiceProvider.GetRequiredService<IProCursorTokenUsageRebuildService>();
            rebuildService.RebuildAsync(
                    factory.ClientId,
                    Arg.Any<ProCursorTokenUsageRebuildRequest>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    new ProCursorTokenUsageRebuildResponse(
                        new DateOnly(2026, 4, 1),
                        new DateOnly(2026, 4, 30),
                        12,
                        new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero)));
        }

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/rebuild")
        {
            Content = JsonContent.Create(
                new
                {
                    from = "2026-04-01",
                    to = "2026-04-30",
                    includeMonthly = true,
                }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(12, body.GetProperty("recomputedBucketCount").GetInt32());
        Assert.Equal("2026-04-01", body.GetProperty("from").GetString());
        Assert.Equal("2026-04-30", body.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Rebuild_ClientUser_Returns403()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/token-usage/rebuild")
        {
            Content = JsonContent.Create(
                new
                {
                    from = "2026-04-01",
                    to = "2026-04-30",
                    includeMonthly = true,
                }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task SeedUsageEventAsync(
        Guid sourceId,
        string sourceDisplayName,
        DateTimeOffset occurredAtUtc,
        long promptTokens,
        long completionTokens,
        decimal estimatedCostUsd,
        string? sourcePath = null,
        string modelName = "text-embedding-3-small",
        string? resourceId = null,
        string? safeMetadataJson = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.ProCursorTokenUsageEvents.Add(
            new ProCursorTokenUsageEvent(
                Guid.NewGuid(),
                factory.ClientId,
                sourceId,
                sourceDisplayName,
                $"test:{sourceId:N}:{occurredAtUtc:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}",
                occurredAtUtc,
                ProCursorTokenUsageCallType.Embedding,
                modelName,
                modelName,
                "cl100k_base",
                promptTokens,
                completionTokens,
                true,
                estimatedCostUsd,
                true,
                resourceId: resourceId,
                sourcePath: sourcePath,
                safeMetadataJson: safeMetadataJson));
        await db.SaveChangesAsync();
    }

    private async Task SeedRollupAsync(DateOnly bucketStartDate, DateTimeOffset recomputedAtUtc)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.ProCursorTokenUsageRollups.Add(
            new ProCursorTokenUsageRollup(
                Guid.NewGuid(),
                factory.ClientId,
                null,
                null,
                bucketStartDate,
                ProCursorTokenUsageGranularity.Daily,
                "text-embedding-3-small",
                120,
                0,
                0.00012m,
                1,
                0,
                recomputedAtUtc));
        await db.SaveChangesAsync();
    }
}
