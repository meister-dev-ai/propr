// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
        Assert.Equal(2, opened!.Response.Views[0].Series[0].Points[0].Score.Counts.Outcomes.Positive);
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
