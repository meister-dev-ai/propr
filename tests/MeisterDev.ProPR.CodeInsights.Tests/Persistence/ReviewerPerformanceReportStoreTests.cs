// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Tests.Persistence;

public sealed class ReviewerPerformanceReportStoreTests
{
    [Fact]
    public async Task RiskSelectionsAndCapturedFacetsSurviveEvidenceChanges()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var cell = Cell(client);
        cell.Impact = FindingImpact.High;
        cell.Likelihood = FindingLikelihood.Likely;
        cell.RiskSeverity = FindingRiskSeverity.High;
        cell.Confidence = FindingConfidence.Certain;
        db.Add(cell);
        await db.SaveChangesAsync();
        var request = Request([client]);
        request = request with
        {
            Query = request.Query with
            {
                Views = [request.Query.Views[0] with { Impacts = ["High"], Likelihoods = ["Likely"], RiskSeverities = ["High"], Confidences = ["Certain"] }]
            }
        };
        var store = new ReviewerPerformanceReportStore(db, new(db));
        await store.SaveAsync(request, [client]);
        cell.Impact = FindingImpact.Low;
        cell.Confidence = FindingConfidence.Low;
        await db.SaveChangesAsync();
        var opened = await store.GetAsync(request.Id, [client]);
        Assert.Equal("High", Assert.Single(opened!.Response.Query.Views[0].Impacts!));
        Assert.Equal("High", Assert.Single(opened.Response.Views[0].Facets.Impacts!).Id);
        Assert.Equal("Certain", Assert.Single(opened.Response.Query.Views[0].Confidences!));
        Assert.Equal("Certain", Assert.Single(opened.Response.Views[0].Facets.Confidences!).Id);
        Assert.Equal(2, opened.Response.Views[0].Series[0].Points[0].OverallPrecision!.Counts.Outcomes.Positive);
    }

    [Fact]
    public async Task LegacyRequestRetryAndPayloadRemainCompatibleWithoutRiskFields()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        db.Add(Cell(client));
        await db.SaveChangesAsync();
        var request = Request([client]);
        var store = new ReviewerPerformanceReportStore(db, new(db));
        var saved = await store.SaveAsync(request, [client]);
        var row = await db.ReviewerPerformanceReports.SingleAsync();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyRequest = JsonNode.Parse(JsonSerializer.Serialize(request, jsonOptions))!;
        foreach (var view in legacyRequest["query"]!["views"]!.AsArray())
        {
            foreach (var property in new[] { "impacts", "likelihoods", "riskSeverities", "confidences" })
            {
                view!.AsObject().Remove(property);
            }
        }

        row.RequestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyRequest.ToJsonString(jsonOptions))));
        var legacyPayload = JsonNode.Parse(row.Payload)!;
        foreach (var view in legacyPayload["views"]!.AsArray())
        {
            foreach (var property in new[] { "impacts", "likelihoods", "riskSeverities", "confidences" })
            {
                view!["facets"]!.AsObject().Remove(property);
            }

            view!["evidence"]!.AsObject().Remove("riskRubricVersions");
        }

        row.Payload = legacyPayload.ToJsonString(jsonOptions);
        var payload = row.Payload;
        await db.SaveChangesAsync();
        var retried = await store.SaveAsync(request, [client]);
        Assert.Equal(saved.Response.CapturedAt, retried.Response.CapturedAt);
        Assert.Null(retried.Response.Views[0].Facets.Impacts);
        Assert.Null(retried.Response.Views[0].Facets.Confidences);
        Assert.Equal(payload, (await db.ReviewerPerformanceReports.SingleAsync()).Payload);
    }

    [Fact]
    public async Task SavedResponseRemainsIdenticalWhenLiveEvidenceChangesAndRetryReturnsOriginal()
    {
        await using var db = Db();
        var a = Guid.NewGuid();
        var cell = Cell(a);
        db.ReviewerPerformanceDailyCounts.Add(cell);
        await db.SaveChangesAsync();
        var store = new ReviewerPerformanceReportStore(db, new(db));
        var request = Request([a]);
        var saved = await store.SaveAsync(request, [a]);
        cell.Count = 400;
        await db.SaveChangesAsync();
        var opened = await store.GetAsync(request.Id, [a]);
        var retried = await store.SaveAsync(request, [a]);
        Assert.Equal(2, opened!.Response.Views[0].Series[0].Points[0].OverallPrecision!.Counts.Outcomes.Positive);
        Assert.Equal(saved.Response.CapturedAt, retried.Response.CapturedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(request with { Name = "Different" }, [a]));
    }

    [Fact]
    public async Task EveryContainedClientIsRequiredToListOpenAndDelete()
    {
        await using var db = Db();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        db.ReviewerPerformanceDailyCounts.AddRange(Cell(a), Cell(b));
        await db.SaveChangesAsync();
        var store = new ReviewerPerformanceReportStore(db, new(db));
        var request = Request([a, b]);
        await store.SaveAsync(request, [a, b]);
        Assert.Empty(await store.ListAsync([a]));
        Assert.Null(await store.GetAsync(request.Id, [a]));
        Assert.False(await store.DeleteAsync(request.Id, [a]));
        Assert.Single(await store.ListAsync([a, b]));
        Assert.True(await store.DeleteAsync(request.Id, [a, b]));
    }

    [Fact]
    public async Task NarrowCaptureCopiesOnlySelectedClientFacets()
    {
        await using var db = Db();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        db.ReviewerPerformanceDailyCounts.AddRange(Cell(a), Cell(b));
        await db.SaveChangesAsync();
        var store = new ReviewerPerformanceReportStore(db, new(db));
        var request = Request([a]);
        await store.SaveAsync(request, [a, b]);
        var opened = await store.GetAsync(request.Id, [a]);
        Assert.NotNull(opened);
        Assert.Single(opened.Response.Views[0].Facets.Clients);
        Assert.Equal(a.ToString(), opened.Response.Views[0].Facets.Clients[0].Id);
    }

    [Fact]
    public async Task ClientPurgeRemovesTheCompleteMixedClientReport()
    {
        await using var db = Db();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var store = new ReviewerPerformanceReportStore(db, new(db));
        var mixed = Request([a, b]);
        var onlyB = Request([b]);
        await store.SaveAsync(mixed, [a, b]);
        await store.SaveAsync(onlyB, [a, b]);
        await store.PurgeForClientAsync(a);
        Assert.Null(await store.GetAsync(mixed.Id, [a, b]));
        Assert.NotNull(await store.GetAsync(onlyB.Id, [b]));
        Assert.DoesNotContain(await db.ReviewerPerformanceReportClients.ToListAsync(), member => member.ReportId == mixed.Id);
    }

    [Fact]
    public async Task MembershiplessReportsCannotBeCapturedOrReadAcrossTenants()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var store = new ReviewerPerformanceReportStore(db, new(db));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Request([]), [client]));
        var legacy = new ReviewerPerformanceReport { Id = Guid.NewGuid(), Name = "Private name", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Add(legacy);
        await db.SaveChangesAsync();
        Assert.Empty(await store.ListAsync([Guid.NewGuid()]));
        Assert.Null(await store.GetAsync(legacy.Id, [client]));
    }

    [Fact]
    public async Task IdenticalRetryCannotOpenSavedReportAfterAllMembershipsWereRemoved()
    {
        await using var db = Db();
        var client = Guid.NewGuid();
        var store = new ReviewerPerformanceReportStore(db, new(db));
        var request = Request([client]);
        await store.SaveAsync(request, [client]);
        db.ReviewerPerformanceReportClients.RemoveRange(await db.ReviewerPerformanceReportClients.ToListAsync());
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(request, [client]));
    }

    private static MeisterProPRDbContext Db() =>
        new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ReviewerPerformanceSaveReportRequest Request(Guid[] clients) => new(
        Guid.NewGuid(), "Review sample", new() { Views = [new() { From = new(2026, 9, 1), To = new(2026, 9, 1), ClientIds = clients }] });

    private static ReviewerPerformanceDailyCount Cell(Guid client) => new()
    {
        Id = Guid.NewGuid(), ClientId = client, CodeInsightPullRequestId = Guid.NewGuid(), BucketDate = new(2026, 9, 1), RepositoryId = "repo",
        PublicationState = CodeInsightPublicationState.Published, Outcome = "positive", Count = 2
    };
}
