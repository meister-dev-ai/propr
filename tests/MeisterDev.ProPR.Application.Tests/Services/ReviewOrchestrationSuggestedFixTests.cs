// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

public partial class ReviewOrchestrationServiceTests
{
    [Theory]
    [InlineData("source();", true)]
    [InlineData("forged source", false)]
    public async Task PublishAsync_ValidatesRunnerFixAgainstServerFetchedSource(string original, bool expected)
    {
        var (jobs, fetcher, reviewer, poster, assignment, clients, scans, _, _, logger) = CreateDeps();
        var job = CreateJob();
        jobs.GetById(job.Id).Returns(job);
        var pr = CreatePullRequest() with
        {
            FetchedHeadSha = "head-sha",
            ChangedFiles = [new ChangedFile("a.cs", ChangeType.Edit, "source();", "@@ -1 +1 @@\n-old();\n+source();")],
        };
        fetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewRevision?>(), Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(pr);
        var finding = new ReviewComment("a.cs", 1, CommentSeverity.Error, "The input fails.")
        {
            SuggestedFix = new ReviewSuggestedFix(1, 1, original, "fixed();", "head-sha", "a.cs", true),
        };
        var service = CreateService(jobs, fetcher, reviewer, poster, assignment, clients, scans, logger);

        await service.PublishAsync(job.Id, new ReviewResult("Review.", [finding]));

        await poster.Received(1).PublishReviewAsync(
            job.ClientId, job.CodeReviewReference, Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result => result.Comments.Count == 1 && result.Comments[0].Message == finding.Message
                                                                      && result.Comments[0].SuggestedFix!.IsSourceValidated == expected),
            Arg.Any<ReviewerIdentity>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewPublicationContext?>());
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.Comments.Count == 1 && result.Comments[0].SuggestedFix!.IsSourceValidated == expected),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-fetched-head")]
    public async Task PublishAsync_UnknownOrDifferentFetchedRevisionCannotValidateRunnerFix(string? fetchedHead)
    {
        var (jobs, fetcher, reviewer, poster, assignment, clients, scans, _, _, logger) = CreateDeps();
        var job = CreateJob();
        jobs.GetById(job.Id).Returns(job);
        var pr = CreatePullRequest() with
        {
            FetchedHeadSha = fetchedHead,
            ChangedFiles = [new ChangedFile("a.cs", ChangeType.Edit, "source();", "@@ -1 +1 @@\n-old();\n+source();")],
        };
        fetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewRevision?>(), Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(pr);
        var finding = new ReviewComment("a.cs", 1, CommentSeverity.Error, "The input fails.")
        {
            SuggestedFix = new ReviewSuggestedFix(1, 1, "source();", "fixed();", "head-sha", "a.cs", true, 1),
        };
        var service = CreateService(jobs, fetcher, reviewer, poster, assignment, clients, scans, logger);
        await service.PublishAsync(job.Id, new ReviewResult("Review.", [finding]));
        await poster.Received(1).PublishReviewAsync(
            job.ClientId, job.CodeReviewReference, Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result => result.Comments.Count == 1 && !result.Comments[0].SuggestedFix!.IsSourceValidated),
            Arg.Any<ReviewerIdentity>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewPublicationContext?>());
    }
}
