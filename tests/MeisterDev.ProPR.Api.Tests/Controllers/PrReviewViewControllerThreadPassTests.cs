// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     An operator inspecting a pull request sees the conversation the thread pass had with the developer, and
///     what it cost, beside the file reviews.
/// </summary>
public sealed class PrReviewViewControllerThreadPassTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private const string ScopePath = "https://dev.azure.com/org";
    private const string ProjectKey = "proj";
    private const string RepositoryId = "repo";
    private const int PullRequestId = 42;

    [Fact]
    public async Task GetPrView_ListsEveryThreadPassOverThePullRequestWithWhatItSpent()
    {
        var completed = CreatePass(iterationId: 2);
        completed.Status = ThreadPassJobStatus.Completed;
        completed.AccumulateSpend(900, 120, 0.75m);
        completed.HandledThreads.Add(
            new ThreadPassHandledThread
            {
                Id = Guid.NewGuid(),
                ThreadPassJobId = completed.Id,
                ClientId = ClientId,
                RepositoryId = RepositoryId,
                PullRequestId = PullRequestId,
                ThreadId = "17",
                ObservedReplyCount = 1,
                RecordedAt = DateTimeOffset.UtcNow,
            });

        var held = CreatePass(iterationId: 3);
        held.Status = ThreadPassJobStatus.BudgetHeld;
        held.SetBudgetBlock(BudgetScopeKind.Increment, BudgetCapKind.Soft, 5m, 6m);

        var controller = CreateController(completed, held);

        var result = await controller.GetPrView(ClientId, Query(), CancellationToken.None);

        var dto = Assert.IsType<PrReviewViewDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(dto.ThreadPasses);
        Assert.Equal(2, dto.ThreadPasses!.Count);

        var completedSummary = dto.ThreadPasses.First(pass => pass.ThreadPassId == completed.Id);
        Assert.Equal(ThreadPassJobStatus.Completed, completedSummary.Status);
        Assert.Equal(1, completedSummary.ThreadCount);
        Assert.Equal(900, completedSummary.TotalInputTokens);
        Assert.Equal(0.75m, completedSummary.TotalEstimatedCostUsd);

        var heldSummary = dto.ThreadPasses.First(pass => pass.ThreadPassId == held.Id);
        Assert.Equal(ThreadPassJobStatus.BudgetHeld, heldSummary.Status);
        Assert.Equal(BudgetScopeKind.Increment, heldSummary.BudgetBlockScope);
        Assert.Equal(5m, heldSummary.BudgetBlockThresholdUsd);

        Assert.Equal(0.75m, dto.ThreadPassTotalEstimatedCostUsd);
        Assert.False(dto.ThreadPassCostIsApproximate);
    }

    [Fact]
    public async Task GetPrView_NoThreadPassHasRun_ReportsNoThreadPassCostRatherThanZero()
    {
        var controller = CreateController();

        var result = await controller.GetPrView(ClientId, Query(), CancellationToken.None);

        var dto = Assert.IsType<PrReviewViewDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Empty(dto.ThreadPasses!);
        Assert.Null(dto.ThreadPassTotalEstimatedCostUsd);
    }

    private static ThreadPassJob CreatePass(int iterationId)
    {
        return new ThreadPassJob(
            Guid.NewGuid(),
            ClientId,
            ScopePath,
            ProjectKey,
            RepositoryId,
            PullRequestId,
            iterationId,
            iterationId.ToString(),
            $"{iterationId}|abc");
    }

    private static GetPrViewQuery Query()
    {
        return new GetPrViewQuery(ScopePath, ProjectKey, RepositoryId, PullRequestId);
    }

    private static PrReviewViewController CreateController(params ThreadPassJob[] passes)
    {
        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByPrAsync(
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var memoryRepository = Substitute.For<IThreadMemoryRepository>();
        memoryRepository.GetDigestsForPullRequestAsync(
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                Arg.Any<MemorySource>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ThreadMemoryDigestDto>([], 0, 1, 50));

        var threadPasses = Substitute.For<IThreadPassJobRepository>();
        threadPasses.GetForPullRequestAsync(
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(passes);

        var controller = new PrReviewViewController(jobRepository, memoryRepository, threadPasses)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();
        controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
        {
            [ClientId] = ClientRole.ClientUser,
        };

        return controller;
    }
}
