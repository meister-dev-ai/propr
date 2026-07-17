// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Support;

public sealed class ReviewBaselineSelectionTests
{
    private static ReviewJob MakeJob(string revisionKey, int iteration, params string[] reviewedPaths)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://scm.example.com/org", "proj", "repo", 42, iteration);
        job.SetReviewRevision(new ReviewRevision($"{revisionKey}-head", "base", null, revisionKey, $"base...{revisionKey}"));
        foreach (var path in reviewedPaths)
        {
            var result = new ReviewFileResult(job.Id, path);
            result.MarkCompleted($"summary for {path}", []);
            job.FileReviewResults.Add(result);
        }

        return job;
    }

    [Fact]
    public void IsFullCoverage_CompletedJob_IsAlwaysFullCoverage()
    {
        var job = MakeJob("rev", 1, "a.cs");
        job.Status = JobStatus.Completed;

        Assert.True(ReviewBaselineSelection.IsFullCoverage(job));
    }

    [Fact]
    public void IsFullCoverage_TerminalJobThatReviewedItsWholeScope_IsFullCoverage()
    {
        var job = MakeJob("rev", 1, "a.cs", "b.cs");
        job.Status = JobStatus.Superseded;
        job.SetInScopeChangedFileCount(2);

        Assert.True(ReviewBaselineSelection.IsFullCoverage(job));
    }

    [Fact]
    public void IsFullCoverage_TerminalJobThatReviewedOnlyPartOfItsScope_IsNotFullCoverage()
    {
        var job = MakeJob("rev", 1, "a.cs");
        job.Status = JobStatus.Superseded;
        job.SetInScopeChangedFileCount(3);

        Assert.False(ReviewBaselineSelection.IsFullCoverage(job));
    }

    [Fact]
    public void IsFullCoverage_NonCompletedJobWithoutScopeCount_IsNotFullCoverage()
    {
        var job = MakeJob("rev", 1, "a.cs");
        job.Status = JobStatus.Failed;

        Assert.False(ReviewBaselineSelection.IsFullCoverage(job));
    }

    [Fact]
    public void SelectReusableBaseline_ExcludesJobsSharingTheCurrentRevisionKey()
    {
        var sameRevision = MakeJob("current", 3, "a.cs", "b.cs", "c.cs");
        sameRevision.Status = JobStatus.Completed;
        var priorRevision = MakeJob("prior", 1, "a.cs");
        priorRevision.Status = JobStatus.Failed;

        var selected = ReviewBaselineSelection.SelectReusableBaseline([sameRevision, priorRevision], "current");

        Assert.Same(priorRevision, selected);
    }

    [Fact]
    public void SelectReusableBaseline_PrefersTheJobWithTheMostUsableReviewedResults()
    {
        var fewer = MakeJob("rev-a", 1, "a.cs");
        fewer.Status = JobStatus.Superseded;
        var more = MakeJob("rev-b", 2, "a.cs", "b.cs", "c.cs");
        more.Status = JobStatus.Failed;

        var selected = ReviewBaselineSelection.SelectReusableBaseline([fewer, more], "current");

        Assert.Same(more, selected);
    }

    [Fact]
    public void SelectReusableBaseline_DeprioritizesAbandonedCancellationBelowOtherTerminalStates()
    {
        // The cancelled job has more usable results, but an abandoned-pull-request cancellation is a poor
        // baseline and ranks below any other terminal state.
        var cancelledRicher = MakeJob("rev-a", 1, "a.cs", "b.cs", "c.cs");
        cancelledRicher.Status = JobStatus.Cancelled;
        var supersededLeaner = MakeJob("rev-b", 2, "a.cs");
        supersededLeaner.Status = JobStatus.Superseded;

        var selected = ReviewBaselineSelection.SelectReusableBaseline([cancelledRicher, supersededLeaner], "current");

        Assert.Same(supersededLeaner, selected);
    }

    [Fact]
    public void SelectReusableBaseline_PrefersCompletedOverOtherTerminalStatesOnEqualUsableCounts()
    {
        var failed = MakeJob("rev-a", 1, "a.cs");
        failed.Status = JobStatus.Failed;
        var completed = MakeJob("rev-b", 2, "a.cs");
        completed.Status = JobStatus.Completed;

        var selected = ReviewBaselineSelection.SelectReusableBaseline([failed, completed], "current");

        Assert.Same(completed, selected);
    }

    [Fact]
    public void SelectReusableBaseline_ReturnsNullWhenEveryCandidateSharesTheCurrentRevision()
    {
        var only = MakeJob("current", 1, "a.cs");
        only.Status = JobStatus.Completed;

        var selected = ReviewBaselineSelection.SelectReusableBaseline([only], "current");

        Assert.Null(selected);
    }
}
