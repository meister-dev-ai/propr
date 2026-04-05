// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Controllers.ProCursor;

public sealed class ProCursorKnowledgeSourceFreshnessTests(
    ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory factory)
    : IClassFixture<ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListSources_WhenLatestSnapshotIsBuilding_ReturnsBuildingFreshness()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            factory.ClientId,
            "Freshness Source",
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            "repo-building",
            "main",
            null,
            true,
            "auto");
        var branch = source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        branch.RecordSeenCommit("commit-building");
        var snapshot = new ProCursorIndexSnapshot(Guid.NewGuid(), source.Id, branch.Id, "commit-building", "full");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ProCursorKnowledgeSources.Add(source);
            db.ProCursorIndexSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("building", body[0].GetProperty("latestSnapshot").GetProperty("freshnessStatus").GetString());
    }

    [Fact]
    public async Task ListTrackedBranches_WhenBranchHeadIsAheadOfIndexedCommit_ReturnsStaleFreshness()
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            factory.ClientId,
            "Freshness Source",
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "project-a",
            "repo-stale",
            "main",
            null,
            true,
            "auto");
        var branch = source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        branch.RecordIndexedCommit("commit-indexed");
        branch.RecordSeenCommit("commit-newer");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.ProCursorKnowledgeSources.Add(source);
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{source.Id}/branches");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("stale", body[0].GetProperty("freshnessStatus").GetString());
    }
}
