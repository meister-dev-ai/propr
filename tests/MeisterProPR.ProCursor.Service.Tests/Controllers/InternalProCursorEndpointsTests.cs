// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.ProCursor.Service.Tests.Support;
using NSubstitute;

namespace MeisterProPR.ProCursor.Service.Tests.Controllers;

public sealed class InternalProCursorEndpointsTests(ProCursorServiceFactory factory)
    : IClassFixture<ProCursorServiceFactory>
{
    [Fact]
    public async Task ListSources_WithSharedKey_DelegatesToGateway()
    {
        var clientId = Guid.NewGuid();
        factory.Gateway.ListSourcesAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ProCursorKnowledgeSourceDto(
                    Guid.NewGuid(),
                    clientId,
                    "Knowledge Repo",
                    ProCursorSourceKind.Repository,
                    "https://dev.azure.com/test-org",
                    "project-a",
                    "repo-a",
                    "main",
                    null,
                    true,
                    "auto",
                    null,
                    [],
                    null,
                    null,
                    "Knowledge Repo"),
            ]);

        var client = this.CreateAuthenticatedClient();
        var response = await client.GetAsync($"/internal/procursor/clients/{clientId:D}/sources");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Knowledge Repo", payload[0].GetProperty("displayName").GetString());
        await factory.Gateway.Received(1).ListSourcesAsync(clientId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueRefresh_WhenGatewayReportsMissingSource_Returns404()
    {
        var clientId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        factory.Gateway.QueueRefreshAsync(clientId, sourceId, Arg.Any<ProCursorRefreshRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProCursorIndexJobDto>>(_ => throw new KeyNotFoundException());

        var client = this.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            $"/internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/refresh",
            new ProCursorRefreshRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AskKnowledge_WhenGatewayUnavailable_Returns503()
    {
        factory.Gateway.AskKnowledgeAsync(Arg.Any<ProCursorKnowledgeQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ProCursorKnowledgeAnswerDto>>(_ => throw new ProCursorDependencyUnavailableException("remote unavailable"));

        var client = this.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            "/internal/procursor/queries/knowledge",
            new ProCursorKnowledgeQueryRequest(Guid.NewGuid(), "Where is caching handled?"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetSymbolInsight_WhenGatewaySucceeds_Returns200()
    {
        factory.Gateway.GetSymbolInsightAsync(Arg.Any<ProCursorSymbolQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorSymbolInsightDto(
                    "complete",
                    Guid.NewGuid(),
                    true,
                    true,
                    new ProCursorSymbolMatchDto(
                        "T:Demo.Greeter",
                        "Greeter",
                        "type",
                        "csharp",
                        "Demo.Greeter",
                        new ProCursorSourceLocationDto("src/Greeter.cs", 3, 12)),
                    [],
                    "fresh"));

        var client = this.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            "/internal/procursor/queries/symbols",
            new ProCursorSymbolQueryRequest(
                Guid.NewGuid(),
                "Greeter",
                "qualifiedName",
                null,
                "reviewTarget",
                new ProCursorReviewContextDto("repo-a", "feature/test", 42, 3),
                10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("complete", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetClientTokenUsage_WithSharedKey_DelegatesToReadRepository()
    {
        var clientId = Guid.NewGuid();
        factory.TokenUsageReadRepository.GetClientUsageAsync(
                clientId,
                new DateOnly(2026, 4, 1),
                new DateOnly(2026, 4, 30),
                ProCursorTokenUsageGranularity.Daily,
                "source",
                Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorTokenUsageResponse(
                    clientId,
                    new DateOnly(2026, 4, 1),
                    new DateOnly(2026, 4, 30),
                    ProCursorTokenUsageGranularity.Daily,
                    "source",
                    new ProCursorTokenUsageTotalsDto(120, 0, 120, 0.00012m, 1, 0),
                    [],
                    [],
                    false,
                    false,
                    new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero)));

        var client = this.CreateAuthenticatedClient();
        var response = await client.GetAsync(
            $"/internal/procursor/clients/{clientId:D}/token-usage?from=2026-04-01&to=2026-04-30&granularity=daily&groupBy=source");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(120, payload.GetProperty("totals").GetProperty("totalTokens").GetInt64());
        await factory.TokenUsageReadRepository.Received(1).GetClientUsageAsync(
            clientId,
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 4, 30),
            ProCursorTokenUsageGranularity.Daily,
            "source",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebuildTokenUsage_WithSharedKey_DelegatesToRebuildService()
    {
        var clientId = Guid.NewGuid();
        factory.TokenUsageRebuildService.RebuildAsync(
                clientId,
                Arg.Any<ProCursorTokenUsageRebuildRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ProCursorTokenUsageRebuildResponse(
                    new DateOnly(2026, 4, 1),
                    new DateOnly(2026, 4, 30),
                    4,
                    new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero)));

        var client = this.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            $"/internal/procursor/clients/{clientId:D}/token-usage/rebuild",
            new ProCursorTokenUsageRebuildRequest(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(4, payload.GetProperty("recomputedBucketCount").GetInt32());
        await factory.TokenUsageRebuildService.Received(1).RebuildAsync(
            clientId,
            Arg.Is<ProCursorTokenUsageRebuildRequest>(request =>
                request.From == new DateOnly(2026, 4, 1)
                && request.To == new DateOnly(2026, 4, 30)
                && request.IncludeMonthly),
            Arg.Any<CancellationToken>());
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, ProCursorServiceFactory.SharedKey);
        return client;
    }
}
