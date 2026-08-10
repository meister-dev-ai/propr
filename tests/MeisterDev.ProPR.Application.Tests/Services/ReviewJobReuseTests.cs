// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Adoption must be idempotent against rows the job already carries. A job can reach adoption with
///     rows written — dispatched to a runner, adopted there, then refused and later run in-process — and
///     the file-result store holds one row per file: adding the same path again fails the review outright
///     with a constraint violation instead of running it.
/// </summary>
public sealed class ReviewJobReuseTests
{
    private static readonly Guid JobId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private readonly IReviewJobExecutionStore _store = Substitute.For<IReviewJobExecutionStore>();
    private readonly IReviewPrScanWatermarkStore _scans = Substitute.For<IReviewPrScanWatermarkStore>();

    private static ReviewJob MakeJob(Guid? id = null)
    {
        var job = new ReviewJob(id ?? JobId, Guid.NewGuid(), "https://forge.invalid/org", "project", "repo", 42, 1);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "rev-1", "base-sha...head-sha"));
        return job;
    }

    private static ReviewJob WithCompletedRows(ReviewJob job, params string[] paths)
    {
        foreach (var path in paths)
        {
            var row = new ReviewFileResult(job.Id, path);
            row.MarkCompleted("done", [], ["pass-1"]);
            job.FileReviewResults.Add(row);
        }

        return job;
    }

    private ReviewJobReuse CreateReuse(ReviewJob jobWithExistingRows)
    {
        this._store.GetByIdWithFileResultsAsync(jobWithExistingRows.Id, Arg.Any<CancellationToken>())
            .Returns(jobWithExistingRows);
        return new ReviewJobReuse(this._store, this._scans, NullLogger.Instance);
    }

    [Fact]
    public async Task ResumingAJobThatAlreadyHasRows_AdoptsOnlyTheMissingFiles()
    {
        var job = MakeJob();
        var reuse = this.CreateReuse(WithCompletedRows(MakeJob(), "src/a.cs"));
        var resumeJob = WithCompletedRows(MakeJob(Guid.NewGuid()), "src/a.cs", "src/b.cs");

        var adopted = await reuse.ResumePriorFileResultsAsync(
            job,
            resumeJob,
            changedPathsSet: new HashSet<string>(["src/a.cs", "src/b.cs"], StringComparer.OrdinalIgnoreCase),
            claimedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Equal(1, adopted);
        await this._store.Received(1).AddFileResultAsync(
            Arg.Is<ReviewFileResult>(row => row.FilePath == "src/b.cs"),
            Arg.Any<CancellationToken>());
        await this._store.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(row => row.FilePath == "src/a.cs"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CarryingForwardOverAJobThatAlreadyHasRows_SkipsThem()
    {
        var job = MakeJob();
        var reuse = this.CreateReuse(WithCompletedRows(MakeJob(), "src/kept.cs"));
        var baseline = WithCompletedRows(MakeJob(Guid.NewGuid()), "src/kept.cs", "src/other.cs");

        var carried = await reuse.CarryForwardBaselineResultsAsync(
            job,
            baseline,
            baselineIsFullCoverage: false,
            changedPathsSet: new HashSet<string>(["src/kept.cs", "src/other.cs"], StringComparer.OrdinalIgnoreCase),
            ReviewExclusionRules.Default,
            claimedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Equal(["src/other.cs"], carried);
        await this._store.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(row => row.FilePath == "src/kept.cs"),
            Arg.Any<CancellationToken>());
    }
}
