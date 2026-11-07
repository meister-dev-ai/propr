// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Adoption must be idempotent against rows the job already carries. A job can reach adoption with rows
///     already written, for example dispatched to a runner, adopted there, then refused and later run
///     in-process, and the file-result store holds one row per file: adding the same path again fails the
///     review with a constraint violation instead of running it.
/// </summary>
public sealed class ReviewJobReuseTests
{
    private static readonly Guid JobId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid ClientId = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private readonly IReviewJobExecutionStore _store = Substitute.For<IReviewJobExecutionStore>();
    private readonly IReviewPrScanWatermarkStore _scans = Substitute.For<IReviewPrScanWatermarkStore>();

    private static ReviewJob MakeJob(Guid? id = null, Guid? clientId = null)
    {
        var job = new ReviewJob(id ?? JobId, clientId ?? ClientId, "https://forge.invalid/org", "project", "repo", 42, 1);
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
        return new ReviewJobReuse(
            this._store, this._scans, NullLogger.Instance,
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry);
    }

    [Fact]
    public async Task ForeignClientResults_AreNeverResumedOrCarriedForward()
    {
        var job = MakeJob();
        var foreign = WithCompletedRows(MakeJob(Guid.NewGuid(), Guid.NewGuid()), "src/a.cs");
        var reuse = this.CreateReuse(job);
        var paths = new HashSet<string>(["src/a.cs"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(0, await reuse.ResumePriorFileResultsAsync(job, foreign, paths, new HashSet<string>(), CancellationToken.None));
        Assert.Empty(
            await reuse.CarryForwardBaselineResultsAsync(
                job, foreign, false, paths, new HashSet<string>(), ReviewExclusionRules.Default, new HashSet<string>(), CancellationToken.None));
        await this._store.DidNotReceive().AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverlappingClients_CanQueueTheSameRevisionIndependently()
    {
        var store = new InMemoryReviewJobRepository();
        var first = MakeJob();
        var second = MakeJob(Guid.NewGuid(), Guid.NewGuid());
        await store.AddAsync(first);

        var result = await store.TryAddIfNoActiveDuplicateAsync(second);

        Assert.True(result.WasAdded);
        Assert.Null(result.DuplicateJob);
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

    // A failed file review leaves no result to carry forward, and a delta fetch contains only the files that
    // changed since the baseline. The reuse state names the failed files, so the delta fetch adds them.
    [Fact]
    public async Task ADeltaScopedBaseline_NamesTheFilesWhoseReviewFailedInIt()
    {
        var job = MakeIterationJob(Guid.NewGuid(), iteration: 2);
        var baseline = MakeIterationJob(Guid.NewGuid(), iteration: 1);
        baseline.Status = JobStatus.Completed;
        WithCompletedRows(baseline, "src/a.cs");
        WithFailedRow(baseline, "src/c.cs");
        this.GivenTheBaseline(baseline);

        var state = await this.CreateReuse(job).LoadScanStateAsync(job, CancellationToken.None);

        Assert.True(state.BaselineIsFullCoverage);
        Assert.Equal(1, state.CompareToIterationId);
        Assert.Equal(["src/c.cs"], state.FailedBaselineFilePaths);
    }

    // The review of a partial baseline fetches the whole pull request, which contains the failed files already.
    [Fact]
    public async Task APartialBaseline_NamesNoFailedFiles()
    {
        var job = MakeIterationJob(Guid.NewGuid(), iteration: 2);
        var baseline = MakeIterationJob(Guid.NewGuid(), iteration: 1);
        baseline.Status = JobStatus.Superseded;
        WithCompletedRows(baseline, "src/a.cs");
        WithFailedRow(baseline, "src/c.cs");
        this.GivenTheBaseline(baseline);

        var state = await this.CreateReuse(job).LoadScanStateAsync(job, CancellationToken.None);

        Assert.False(state.BaselineIsFullCoverage);
        Assert.Null(state.CompareToIterationId);
        Assert.Empty(state.FailedBaselineFilePaths);
    }

    private static ReviewJob MakeIterationJob(Guid id, int iteration)
    {
        var job = new ReviewJob(id, ClientId, "https://dev.azure.com/org", "project", "repo", 42, iteration);
        var revision = iteration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        job.SetReviewRevision(new ReviewRevision($"head-{revision}", "base-sha", null, revision, null));
        return job;
    }

    private static void WithFailedRow(ReviewJob job, string path)
    {
        var row = new ReviewFileResult(job.Id, path);
        row.MarkFailed("The model endpoint refused the request.");
        job.FileReviewResults.Add(row);
    }

    private void GivenTheBaseline(ReviewJob baseline)
    {
        this._store.GetLatestReusableTerminalJobAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(baseline);
    }

    // Against a partial baseline the review fetches the whole pull request. A reviewed file carries forward when
    // the pull request still contains it and it did not change since the baseline. A reviewed file that changed
    // keeps no result from its previous content and is reviewed again.
    [Fact]
    public async Task APartialBaseline_CarriesForwardOnlyTheReviewedFilesThatDidNotChange()
    {
        var job = MakeJob();
        var reuse = this.CreateReuse(job);
        var baseline = WithCompletedRows(MakeJob(Guid.NewGuid()), "src/unchanged.cs", "src/changed.cs", "src/removed.cs");

        var carried = await reuse.CarryForwardBaselineResultsAsync(
            job,
            baseline,
            baselineIsFullCoverage: false,
            changedPathsSet: new HashSet<string>(["src/unchanged.cs", "src/changed.cs", "src/new.cs"], StringComparer.OrdinalIgnoreCase),
            pathsChangedSinceBaseline: new HashSet<string>(["src/changed.cs", "src/new.cs", "src/removed.cs"], StringComparer.OrdinalIgnoreCase),
            ReviewExclusionRules.Default,
            claimedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Equal(["src/unchanged.cs"], carried);
    }

    // Without the comparison of the two revisions, no reviewed file can be shown to be unchanged, so none carries
    // forward and the review covers every file.
    [Fact]
    public async Task APartialBaseline_WithoutTheChangedPaths_CarriesNothingForward()
    {
        var job = MakeJob();
        var reuse = this.CreateReuse(job);
        var baseline = WithCompletedRows(MakeJob(Guid.NewGuid()), "src/unchanged.cs");

        var carried = await reuse.CarryForwardBaselineResultsAsync(
            job,
            baseline,
            baselineIsFullCoverage: false,
            changedPathsSet: new HashSet<string>(["src/unchanged.cs"], StringComparer.OrdinalIgnoreCase),
            pathsChangedSinceBaseline: null,
            ReviewExclusionRules.Default,
            claimedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Empty(carried);
        await this._store.DidNotReceive().AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>());
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
            pathsChangedSinceBaseline: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ReviewExclusionRules.Default,
            claimedPaths: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);

        Assert.Equal(["src/other.cs"], carried);
        await this._store.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(row => row.FilePath == "src/kept.cs"),
            Arg.Any<CancellationToken>());
    }
}
