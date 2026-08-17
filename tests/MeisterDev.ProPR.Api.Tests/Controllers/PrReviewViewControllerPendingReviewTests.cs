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
///     Whether a pull request is waiting for a review is answered once, here, so that the ProPR UI and the
///     browser extension offer the action on the same terms rather than each deciding for itself.
/// </summary>
public sealed class PrReviewViewControllerPendingReviewTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private const string ScopePath = "https://dev.azure.com/org";
    private const string ProjectKey = "proj";
    private const string RepositoryId = "repo";
    private const int PullRequestId = 42;

    [Fact]
    public async Task GetPrView_RevisionDeclinedAndNotSinceReviewed_ReportsThePullRequestAsWaiting()
    {
        var detectedAt = DateTimeOffset.UtcNow.AddHours(-3);
        var controller = CreateController(CreateScan(reviewedRevision: "iter-1", pendingRevision: "iter-2", detectedAt));

        var dto = await GetViewAsync(controller);

        Assert.NotNull(dto.PendingReview);
        Assert.Equal("iter-2", dto.PendingReview!.RevisionKey);
        Assert.Equal("iter-1", dto.PendingReview.ReviewedRevisionKey);
        Assert.Equal(detectedAt, dto.PendingReview.DetectedAt);
    }

    /// <summary>
    ///     A review of the declined revision retires the state by writing its own watermark over the same value.
    ///     Reporting it as still waiting would offer a review of what was just reviewed.
    /// </summary>
    [Fact]
    public async Task GetPrView_DeclinedRevisionHasSinceBeenReviewed_ReportsNothingWaiting()
    {
        var controller = CreateController(CreateScan(reviewedRevision: "iter-2", pendingRevision: "iter-2", DateTimeOffset.UtcNow));

        var dto = await GetViewAsync(controller);

        Assert.Null(dto.PendingReview);
    }

    /// <summary>
    ///     A pull request reviewed at its only revision was never declined, so nothing is waiting and the
    ///     surfaces show no action.
    /// </summary>
    [Fact]
    public async Task GetPrView_NothingWasEverDeclined_ReportsNothingWaiting()
    {
        var controller = CreateController(CreateScan(reviewedRevision: "iter-1", pendingRevision: string.Empty, detectedAt: null));

        var dto = await GetViewAsync(controller);

        Assert.Null(dto.PendingReview);
    }

    [Fact]
    public async Task GetPrView_PullRequestHasNoScanRecord_ReportsNothingWaiting()
    {
        var controller = CreateController(scan: null);

        var dto = await GetViewAsync(controller);

        Assert.Null(dto.PendingReview);
    }

    /// <summary>
    ///     A pull request the thread pass reached but no file review ever did has a pending revision and no
    ///     reviewed one. It is waiting, and the surface says so without inventing a revision it was reviewed at.
    /// </summary>
    [Fact]
    public async Task GetPrView_DeclinedWithNoReviewOnRecord_ReportsWaitingWithNoReviewedRevision()
    {
        var controller = CreateController(CreateScan(reviewedRevision: string.Empty, pendingRevision: "iter-2", DateTimeOffset.UtcNow));

        var dto = await GetViewAsync(controller);

        Assert.NotNull(dto.PendingReview);
        Assert.Equal("iter-2", dto.PendingReview!.RevisionKey);
        Assert.Null(dto.PendingReview.ReviewedRevisionKey);
    }

    private static async Task<PrReviewViewDto> GetViewAsync(PrReviewViewController controller)
    {
        var result = await controller.GetPrView(
            ClientId,
            new GetPrViewQuery(ScopePath, ProjectKey, RepositoryId, PullRequestId),
            CancellationToken.None);

        return Assert.IsType<PrReviewViewDto>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static ReviewPrScan CreateScan(
        string reviewedRevision,
        string pendingRevision,
        DateTimeOffset? detectedAt)
    {
        return new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", RepositoryId, PullRequestId, "seed")
        {
            LastProcessedCommitId = reviewedRevision,
            PendingReviewRevisionKey = pendingRevision,
            PendingReviewDetectedAt = detectedAt,
        };
    }

    private static PrReviewViewController CreateController(ReviewPrScan? scan)
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
            .Returns([]);

        var prScanReader = Substitute.For<IReviewPrScanReader>();
        prScanReader.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), RepositoryId, PullRequestId, Arg.Any<CancellationToken>())
            .Returns(scan);

        var controller = new PrReviewViewController(
            jobRepository,
            memoryRepository,
            threadPasses,
            prScanReader)
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
