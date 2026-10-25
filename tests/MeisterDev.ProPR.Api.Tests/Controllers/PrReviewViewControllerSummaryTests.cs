// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

public sealed class PrReviewViewControllerSummaryTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private const string Scope = "https://provider.example";
    private const string Project = "project";
    private const string Repository = "repository";
    private const int PullRequest = 373;

    [Fact]
    public async Task GetPrView_PreservesJobUsageBreakdownAndNullableCostRollups()
    {
        var first = CreateJob(
            100, 10,
            new TokenBreakdownEntry(AiConnectionModelCategory.HighEffort, "model", 100, 10, EstimatedCostUsd: 0.75m));
        var second = CreateJob(
            50, 5,
            new TokenBreakdownEntry(AiConnectionModelCategory.HighEffort, "model", 50, 5));
        var (controller, _, _) = CreateController(new PrReviewViewData([first, second], [], [], null));

        var dto = await GetViewAsync(controller);

        Assert.Equal(2, dto.TotalJobs);
        Assert.Equal(150, dto.TotalInputTokens);
        Assert.Equal(15, dto.TotalOutputTokens);
        Assert.Equal(new[] { first, second }, dto.Jobs);
        var breakdown = Assert.Single(dto.AggregatedTokenBreakdown);
        Assert.Equal(150, breakdown.TotalInputTokens);
        Assert.Equal(15, breakdown.TotalOutputTokens);
        Assert.Equal(0.75m, breakdown.EstimatedCostUsd);
        Assert.True(breakdown.CostIsApproximate);
        Assert.True(dto.BreakdownConsistent);
        Assert.Equal(0.75m, dto.TotalEstimatedCostUsd);
        Assert.True(dto.CostIsApproximate);
        Assert.Null(dto.ThreadPassTotalEstimatedCostUsd);
    }

    [Fact]
    public async Task GetPrView_ResolvesDeduplicatedContributionIdsAndPreservesMemoryExcerpts()
    {
        var originatedId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var removedId = Guid.NewGuid();
        var sample = JsonSerializer.Serialize(new { contributingMemoryIds = new[] { originatedId, externalId, externalId, removedId } });
        var (controller, _, memory) = CreateController(new PrReviewViewData([], [sample, "invalid json", "{\"contributingMemoryIds\":null}"], [], null));
        var originated = CreateMemory(originatedId, new string('a', 300));
        var external = CreateMemory(externalId, new string('b', 300));
        memory.GetDigestsForPullRequestAsync(
                ClientId, Scope, Project, Repository, PullRequest,
                MemorySource.ThreadResolved, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ThreadMemoryDigestDto>([originated], 53, 1, 50));
        memory.GetDigestsByIdsAsync(ClientId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([external]);

        var dto = await GetViewAsync(controller);

        Assert.Equal(53, dto.OriginatedMemoryCount);
        Assert.Equal(new string('a', 200), Assert.Single(dto.OriginatedMemories).ResolutionSummaryExcerpt);
        Assert.Equal(2, dto.ContributedMemoryCount);
        Assert.Equal(externalId, Assert.Single(dto.ContributedMemories).MemoryRecordId);
        Assert.Equal(new string('b', 200), dto.ContributedMemories[0].ResolutionSummaryExcerpt);
        await memory.Received(1).GetDigestsByIdsAsync(
            ClientId,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(externalId) && ids.Contains(removedId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPrView_ClampsPagingAndPassesTheRequestCancellationToken()
    {
        var (controller, reader, _) = CreateController(new PrReviewViewData([], [], [], null));
        using var cancellation = new CancellationTokenSource();

        await controller.GetPrView(ClientId, new GetPrViewQuery(Scope, Project, Repository, PullRequest, Page: -3, PageSize: 500), cancellation.Token);

        await reader.Received(1).ReadAsync(ClientId, Scope, Project, Repository, PullRequest, 1, 100, cancellation.Token);
    }

    private static PrJobSummaryDto CreateJob(long input, long output, TokenBreakdownEntry breakdown) =>
        new(Guid.NewGuid(), JobStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, input, output, [breakdown]);

    private static ThreadMemoryDigestDto CreateMemory(Guid id, string summary) =>
        new(
            id, "thread", MemorySource.ThreadResolved, Repository, PullRequest, "src/File.cs", summary,
            DateTimeOffset.UtcNow, null, null);

    private static async Task<PrReviewViewDto> GetViewAsync(PrReviewViewController controller)
    {
        var result = await controller.GetPrView(ClientId, new GetPrViewQuery(Scope, Project, Repository, PullRequest));
        return Assert.IsType<PrReviewViewDto>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static (PrReviewViewController Controller, IPrReviewViewReader Reader, IThreadMemoryRepository Memory) CreateController(PrReviewViewData data)
    {
        var reader = Substitute.For<IPrReviewViewReader>();
        reader.ReadAsync(ClientId, Scope, Project, Repository, PullRequest, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(data);
        var memory = Substitute.For<IThreadMemoryRepository>();
        memory.GetDigestsForPullRequestAsync(
                ClientId, Scope, Project, Repository, PullRequest,
                MemorySource.ThreadResolved, 50, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ThreadMemoryDigestDto>([], 0, 1, 50));
        var controller = new PrReviewViewController(reader, memory)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [ClientId] = ClientRole.ClientUser };
        return (controller, reader, memory);
    }
}
