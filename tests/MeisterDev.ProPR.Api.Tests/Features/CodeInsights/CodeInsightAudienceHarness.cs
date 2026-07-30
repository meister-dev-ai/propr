// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.CodeInsights.Controllers;
using MeisterDev.ProPR.Api.Features.CodeInsights.Support;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;
using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Application.Features.CodeInsights.Survival;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.CodeInsights;

/// <summary>
///     Shared fixture for the two code-insight audiences. Both controllers read the same collected facts through
///     the same readers; what differs is who may ask, so the harness makes the caller's identity the parameter.
/// </summary>
internal sealed class CodeInsightAudienceHarness
{
    public static readonly Guid MineA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    public static readonly Guid MineB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    public static readonly Guid SomebodyElses = Guid.Parse("cccccccc-3333-3333-3333-333333333333");
    public static readonly Guid MyTenant = Guid.Parse("dddddddd-4444-4444-4444-444444444444");
    public static readonly Guid OtherTenant = Guid.Parse("eeeeeeee-5555-5555-5555-555555555555");

    public CodeInsightAudienceHarness(
        bool licensed = true,
        bool withLicensingService = true,
        bool withReaders = true,
        bool authenticated = true,
        bool withClientRoles = true,
        bool tenantAdmin = false,
        bool isAdmin = false,
        string? minimumSampleSize = null)
    {
        this.Rollups = Substitute.For<ICodeInsightRollupReader>();
        this.Metrics = Substitute.For<ICodeInsightMetricReader>();
        this.Browse = Substitute.For<ICodeInsightBrowseReader>();
        this.Survival = Substitute.For<ICodeInsightSurvivalReader>();
        this.Clients = Substitute.For<IClientAdminService>();
        this.Licensing = Substitute.For<ILicensingCapabilityService>();

        this.Rollups
            .GetSeriesAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedRollupScopes.Add),
                Arg.Any<CodeInsightCountDimension>(),
                Arg.Do<CodeInsightBucketSize>(this.RequestedBuckets.Add),
                Arg.Any<CancellationToken>())
            .Returns([new CodeInsightSeriesPoint(new DateOnly(2026, 6, 1), "logic-error", 3)]);

        this.Rollups
            .GetConcentrationAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedRollupScopes.Add),
                Arg.Do<CodeInsightGrain>(this.RequestedGrains.Add),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        this.Rollups
            .GetTotalAsync(Arg.Any<CodeInsightRollupQuery>(), Arg.Any<CancellationToken>())
            .Returns(3);

        this.WithCorrectnessSeries();
        this.WithByGrain();

        // Stubbed here rather than per test, so a test that only exercises the endpoint's plumbing does not have to
        // know that an unstubbed substitute hands back a null report.
        this.Rollups
            .GetHotspotsAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedRollupScopes.Add),
                Arg.Do<long?>(this.RequestedHotspotFileSelectors.Add),
                Arg.Any<int>(),
                Arg.Do<CodeInsightHotspotGrouping>(this.RequestedHotspotGroupings.Add),
                Arg.Any<CancellationToken>())
            .Returns(_ => this._hotspots);

        this.Survival
            .GetSurvivalAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedSurvivalScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns(new CodeInsightSurvivalCounts(9, 3, 2, 3));

        this.Survival
            .GetSurvivalByPullRequestAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedSurvivalScopes.Add),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new CodeInsightPullRequestSurvival(MineA, "repo-1", 4790, 3, new CodeInsightSurvivalCounts(2, 1, 2, 1)),
            ]);

        this.Metrics
            .GetAcceptanceSeriesAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Any<CodeInsightBucketSize>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        this.Metrics
            .GetCorrectnessAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns(Result(0.5, 12));

        this.Metrics
            .GetAcceptanceAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns(Result(0.5, 40));

        this.Metrics
            .GetRejectionReasonsAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns(CodeInsightRejectionReasonBreakdown.Empty);

        this.Browse
            .ListFindingsAsync(
                Arg.Do<CodeInsightBrowseQuery>(this.RequestedBrowseScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns([]);

        this.Browse
            .ListMissesAsync(
                Arg.Do<CodeInsightBrowseQuery>(this.RequestedBrowseScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns([Miss(true), Miss(false)]);

        this.Clients.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Client(MineA, "Client A", MyTenant),
            Client(MineB, "Client B", MyTenant),
            Client(SomebodyElses, "Somebody Else", OtherTenant),
        ]);

        this.Clients.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ClientDto>>(
                ((IEnumerable<Guid>)call[0])
                .Select(id => Client(id, id == MineA ? "Client A" : id == MineB ? "Client B" : "Somebody Else", MyTenant))
                .ToList()));

        this.Licensing
            .IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(licensed));

        var httpContext = new DefaultHttpContext();
        if (authenticated)
        {
            httpContext.Items["UserId"] = Guid.NewGuid().ToString();
            httpContext.Items["IsAdmin"] = isAdmin;
            if (withClientRoles)
            {
                httpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
                {
                    [MineA] = ClientRole.ClientUser,
                    [MineB] = ClientRole.ClientAdministrator,
                };
            }

            if (tenantAdmin)
            {
                httpContext.Items["TenantRoles"] = new Dictionary<Guid, TenantRole>
                {
                    [MyTenant] = TenantRole.TenantAdministrator,
                    [OtherTenant] = TenantRole.TenantUser,
                };
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                minimumSampleSize is null
                    ? []
                    : new Dictionary<string, string?>
                    {
                        ["CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS"] = minimumSampleSize,
                    })
            .Build();

        var resolver = new CodeInsightScopeResolver(
            this.Clients,
            withLicensingService ? this.Licensing : null);

        this.CodeQuality = new CodeQualityController(
            resolver,
            this.Clients,
            withReaders ? this.Rollups : null,
            withReaders ? this.Browse : null,
            withReaders ? this.Survival : null)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        this.ReviewerPerformance = new ReviewerPerformanceController(
            configuration,
            resolver,
            this.Clients,
            withReaders ? this.Metrics : null,
            withReaders ? this.Browse : null)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    public ICodeInsightRollupReader Rollups { get; }

    public ICodeInsightMetricReader Metrics { get; }

    public ICodeInsightBrowseReader Browse { get; }

    public ICodeInsightSurvivalReader Survival { get; }

    public IClientAdminService Clients { get; }

    public ILicensingCapabilityService Licensing { get; }

    public CodeQualityController CodeQuality { get; }

    public ReviewerPerformanceController ReviewerPerformance { get; }

    public List<CodeInsightRollupQuery> RequestedRollupScopes { get; } = [];

    public List<long?> RequestedHotspotFileSelectors { get; } = [];

    public List<CodeInsightHotspotGrouping> RequestedHotspotGroupings { get; } = [];

    private CodeInsightHotspotReport _hotspots = new(0, 0, null, 0, [], 0);

    public List<CodeInsightRollupQuery> RequestedMetricScopes { get; } = [];

    public List<CodeInsightBrowseQuery> RequestedBrowseScopes { get; } = [];

    public List<CodeInsightRollupQuery> RequestedSurvivalScopes { get; } = [];

    public List<CodeInsightBucketSize> RequestedBuckets { get; } = [];

    public List<CodeInsightBucketSize> RequestedMetricBuckets { get; } = [];

    public List<CodeInsightGrain> RequestedGrains { get; } = [];

    public static void AssertExactly(CodeInsightRollupQuery query, params Guid[] expected)
    {
        Assert.Equal(expected.OrderBy(id => id), query.ClientIds.OrderBy(id => id));
    }

    public static void AssertExactly(CodeInsightBrowseQuery query, params Guid[] expected)
    {
        Assert.Equal(expected.OrderBy(id => id), query.ClientIds.OrderBy(id => id));
    }

    public void WithCorrectnessSeries(params (DateOnly Bucket, double F1, int SampleSize)[] points)
    {
        this.Metrics
            .GetCorrectnessSeriesAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Do<CodeInsightBucketSize>(this.RequestedMetricBuckets.Add),
                Arg.Any<CancellationToken>())
            .Returns(
                points
                    .Select(point => new CodeInsightMetricSeriesPoint(point.Bucket, Result(point.F1, point.SampleSize)))
                    .ToList());
    }

    public void WithByGrain(params (string? Repository, long? PullRequest, double? F1, int SampleSize)[] rows)
    {
        this.Metrics
            .GetCorrectnessByGrainAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Do<CodeInsightGrain>(this.RequestedGrains.Add),
                Arg.Any<CancellationToken>())
            .Returns(
                rows
                    .Select(row => new CodeInsightScopedMetricResult(
                        MineA,
                        row.Repository,
                        row.PullRequest,
                        new CodeInsightMetricResult(
                            new CodeInsightMetrics(
                                new CodeInsightMetricInputs(1, 0, 0, 1, 1),
                                row.F1,
                                row.F1,
                                row.F1,
                                row.F1),
                            row.SampleSize)))
                    .ToList());
    }

    /// <summary>
    ///     What the rejection-reason read answers with. The unclassified remainder is passed separately, because
    ///     it is not a reason and the endpoint must not fold it into one.
    /// </summary>
    public void WithRejectionReasons(
        int unclassified,
        params (CodeInsightRejectionReason Reason, int Count)[] counts)
    {
        var byReason = counts.ToDictionary(entry => entry.Reason, entry => entry.Count);
        var rejections = counts.Sum(entry => entry.Count) + unclassified;

        this.Metrics
            .GetRejectionReasonsAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns(
                new CodeInsightRejectionReasonBreakdown(
                    byReason,
                    unclassified,
                    rejections,
                    // One class carrying everything: enough for the endpoint's shape, and the split itself is
                    // the reader's business rather than the controller's.
                    [
                        new CodeInsightConcernClassRejections(
                            CodeInsightConcernClass.Functional,
                            byReason,
                            unclassified,
                            rejections),
                    ]));
    }

    /// <summary>
    ///     Stubs the per-model read. The reader already returns rows the way this grouping is defined: precision
    ///     and acceptance only, sample counted in resolved findings.
    /// </summary>
    public void WithByModel(params (string? ModelId, string? LogicalName, double? Precision, int Findings)[] rows)
    {
        this.Metrics
            .GetByModelAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedMetricScopes.Add),
                Arg.Any<CancellationToken>())
            .Returns(
                rows
                    .Select(row => new CodeInsightModelMetricResult(
                        row.ModelId,
                        row.LogicalName,
                        new CodeInsightMetricResult(
                            new CodeInsightMetrics(
                                new CodeInsightMetricInputs(1, 0, 0, 1, 0),
                                row.Precision,
                                Recall: null,
                                F1: null,
                                row.Precision),
                            row.Findings)))
                    .ToList());
    }

    /// <summary>
    ///     What the hotspot read answers with. Swapped rather than re-stubbed, because the recorders that capture the
    ///     scope, the file selector, and the grouping are registered once in the constructor: configuring the same
    ///     call twice would make every invocation record twice and turn a single-item assertion into a lie.
    /// </summary>
    public void WithHotspots(CodeInsightHotspotReport report)
    {
        this._hotspots = report;
    }

    public void WithConcentration(params CodeInsightConcentrationRow[] rows)
    {
        this.Rollups
            .GetConcentrationAsync(
                Arg.Do<CodeInsightRollupQuery>(this.RequestedRollupScopes.Add),
                Arg.Do<CodeInsightGrain>(this.RequestedGrains.Add),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(rows.ToList());
    }

    /// <summary>
    ///     A metric result whose ratios are the requested value. The inputs are deliberately not made consistent
    ///     with it: these tests are about scoping and presentation, and the arithmetic has its own tests.
    /// </summary>
    private static CodeInsightMetricResult Result(double ratio, int sampleSize)
    {
        return new CodeInsightMetricResult(
            new CodeInsightMetrics(new CodeInsightMetricInputs(1, 0, 0, 1, 1), ratio, ratio, ratio, ratio),
            sampleSize);
    }

    private static CodeInsightMissRow Miss(bool countsAsMiss)
    {
        return new CodeInsightMissRow(
            Guid.NewGuid(),
            MineA,
            "repo-1",
            7,
            countsAsMiss ? "thread-1" : "thread-2",
            "a.cs",
            12,
            "alice: this drops the retry count",
            IsSubstantive: countsAsMiss,
            WasActedOn: countsAsMiss,
            IsInScope: countsAsMiss,
            CountsAsMiss: countsAsMiss,
            0.9,
            DateTimeOffset.UtcNow);
    }

    private static ClientDto Client(Guid id, string displayName, Guid tenantId)
    {
        return new ClientDto(
            id,
            displayName,
            true,
            DateTimeOffset.UtcNow,
            CommentResolutionBehavior.Disabled,
            null,
            null,
            null,
            true)
        {
            TenantId = tenantId,
        };
    }
}
