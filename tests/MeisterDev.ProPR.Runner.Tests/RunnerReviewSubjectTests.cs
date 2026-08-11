// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Runner.Execution;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     Rebuilding what a review is of, from the manifest and the working copy. The pipeline downstream
///     cannot tell which side assembled this, and the point of these tests is that it stays that way.
/// </summary>
public sealed class RunnerReviewSubjectTests
{
    [Fact]
    public void TheJob_CarriesTheIdentityTheControlPlaneKnowsItBy()
    {
        var manifest = RunnerManifests.Sample();

        var job = RunnerReviewSubject.BuildJob(manifest);

        Assert.Equal(manifest.JobId, job.Id);
        Assert.Equal(manifest.ClientId, job.ClientId);
        Assert.Equal(12, job.PullRequestId);
        Assert.Equal(2, job.IterationId);
        Assert.Equal(ScmProvider.Forgejo, job.Provider);
        Assert.Equal("head-sha", job.RevisionHeadSha);
        Assert.Equal("base-sha", job.RevisionBaseSha);
        Assert.Equal("main", job.PrTargetBranch);
    }

    // The scope is frozen at dispatch precisely so a push landing mid-review cannot change what the review
    // is of. Rediffing here would undo that.
    [Fact]
    public async Task TheChangedSet_IsTheManifestsScopeAndNotAFreshDiff()
    {
        var manifest = RunnerManifests.Sample(["src/a.cs"]);
        var workspace = new FakeWorkspace
        {
            Changed =
            [
                new ChangedFileSummary("src/a.cs", ChangeType.Edit),
                new ChangedFileSummary("src/pushed-after-dispatch.cs", ChangeType.Add),
            ],
        };

        var pr = await RunnerReviewSubject.BuildPullRequestAsync(manifest, workspace, CancellationToken.None);

        Assert.Equal(["src/a.cs"], pr.ChangedFiles.Select(file => file.Path));
    }

    [Fact]
    public async Task EachChangedFile_CarriesItsHeadContentAndItsDiff()
    {
        var manifest = RunnerManifests.Sample(["src/a.cs"]);
        var workspace = new FakeWorkspace
        {
            Changed = [new ChangedFileSummary("src/a.cs", ChangeType.Edit)],
            Contents = { ["src/a.cs"] = "class A;" },
            Diffs = { ["src/a.cs"] = "@@ -1 +1 @@" },
        };

        var pr = await RunnerReviewSubject.BuildPullRequestAsync(manifest, workspace, CancellationToken.None);

        var file = Assert.Single(pr.ChangedFiles);
        Assert.Equal("class A;", file.FullContent);
        Assert.Equal("@@ -1 +1 @@", file.UnifiedDiff);
    }

    // A deleted file has no head content. Reading it anyway puts an empty string where the reviewer expects
    // the file that was removed, which reads as "this file is now empty" rather than "this file is gone".
    [Fact]
    public async Task ADeletedFile_IsNotReadFromTheHeadWorktree()
    {
        var manifest = RunnerManifests.Sample(["src/gone.cs"]);
        var workspace = new FakeWorkspace
        {
            Changed = [new ChangedFileSummary("src/gone.cs", ChangeType.Delete)],
            Diffs = { ["src/gone.cs"] = "@@ -1 +0,0 @@" },
        };

        var pr = await RunnerReviewSubject.BuildPullRequestAsync(manifest, workspace, CancellationToken.None);

        Assert.Empty(workspace.Reads);
        Assert.Equal(string.Empty, Assert.Single(pr.ChangedFiles).FullContent);
    }

    // The reviewer reads the conversation to avoid raising again what has already been answered.
    [Fact]
    public async Task TheConversation_ArrivesOnThePullRequest()
    {
        var sample = RunnerManifests.Sample(["src/a.cs"]);
        var manifest = sample with
        {
            Target = sample.Target with
            {
                ExistingThreads =
                [
                    new RunnerReviewThread("src/a.cs", 7, "active", [new RunnerReviewThreadComment("Reviewer", "Bounded?")]),
                ],
            },
        };

        var pr = await RunnerReviewSubject.BuildPullRequestAsync(
            manifest,
            new FakeWorkspace { Changed = [new ChangedFileSummary("src/a.cs", ChangeType.Edit)] },
            CancellationToken.None);

        var thread = Assert.Single(pr.ExistingThreads!);
        Assert.Equal("src/a.cs", thread.FilePath);
        Assert.Equal(7, thread.LineNumber);
        Assert.Equal("Bounded?", Assert.Single(thread.Comments).Content);
    }

    // The profile and temperature live on the job, not the context, and the pipeline reads them there. Before
    // these were seeded, every remote review ran the Balanced profile at default temperature whatever the
    // client had configured, and nothing reported the substitution.
    [Fact]
    public void TheBehaviourSection_LandsOnTheJobsProfileAndTemperature()
    {
        var sample = RunnerManifests.Sample();
        var manifest = sample with
        {
            Behaviour = new RunnerReviewBehaviour(true, false, false, true, 0.3f, "file_by_file_calm"),
        };

        var job = RunnerReviewSubject.BuildJob(manifest);

        Assert.Equal("file_by_file_calm", job.ReviewPipelineProfileId);
        Assert.Equal(0.3f, job.ReviewTemperature);
    }

    // A manifest from an older control plane has no behaviour section, and the job must read exactly as
    // it did before the section existed.
    [Fact]
    public void AManifestWithoutABehaviourSection_LeavesProfileAndTemperatureUnset()
    {
        var job = RunnerReviewSubject.BuildJob(RunnerManifests.Sample());

        Assert.Null(job.ReviewPipelineProfileId);
        Assert.Null(job.ReviewTemperature);
    }

    // Linked items are carried in the manifest because discovery is a credentialed call. The prompt reads them
    // from the pull request exactly as in-process, so they have to be present there in full.
    [Fact]
    public async Task LinkedItems_ArriveOnThePullRequest()
    {
        var sample = RunnerManifests.Sample(["src/a.cs"]);
        var manifest = sample with
        {
            LinkedItems =
            [
                new RunnerLinkedItem(
                    "AB#12", "User Story", "Add the widget", "So the widget exists.", "https://items.invalid/12",
                    [new RunnerLinkedItemRef("Parent", "AB#4", null, "The epic")]),
            ],
        };

        var pr = await RunnerReviewSubject.BuildPullRequestAsync(
            manifest,
            new FakeWorkspace { Changed = [new ChangedFileSummary("src/a.cs", ChangeType.Edit)] },
            CancellationToken.None);

        var item = Assert.Single(pr.LinkedItems!);
        Assert.Equal("AB#12", item.ProviderKey);
        Assert.Equal("User Story", item.ItemType);
        Assert.Equal("So the widget exists.", item.Description);
        var link = Assert.Single(item.RelatedLinks);
        Assert.Equal("Parent", link.Kind);
        Assert.Equal("AB#4", link.TargetKey);
    }

    // Null rather than empty, so the prompt section stays absent exactly as it does in-process when the
    // client keeps linked items out of context or none exist.
    [Fact]
    public async Task AManifestWithoutLinkedItems_LeavesThePullRequestWithout()
    {
        var pr = await RunnerReviewSubject.BuildPullRequestAsync(
            RunnerManifests.Sample(["src/a.cs"]),
            new FakeWorkspace { Changed = [new ChangedFileSummary("src/a.cs", ChangeType.Edit)] },
            CancellationToken.None);

        Assert.Null(pr.LinkedItems);
    }

    // A provider the runner does not know means the control plane is newer than this image. Guessing a
    // provider would review the change as though it lived somewhere else.
    [Fact]
    public void AnUnknownProvider_FailsWithSomethingAnOperatorCanActOn()
    {
        var sample = RunnerManifests.Sample();
        var manifest = sample with { Target = sample.Target with { Provider = "SomethingNewer" } };

        var error = Assert.Throws<InvalidOperationException>(() => RunnerReviewSubject.BuildJob(manifest));

        Assert.Contains("SomethingNewer", error.Message, StringComparison.Ordinal);
    }

    // A reclaimed job resumes from what the earlier attempt recorded. Without this it re-reviews every file
    // and synthesizes only over its own part of the review: the earlier findings stay in the control plane's
    // database and never reach the review that gets posted.
    [Fact]
    public void SeededPriorResults_PutBackWhatAnEarlierAttemptRecorded()
    {
        var job = RunnerReviewSubject.BuildJob(RunnerManifests.Sample());

        RunnerReviewSubject.SeedPriorResults(
            job,
            [
                new RunnerPriorFileResult("src/a.cs", true, false, false, null, null, "looks fine", ["pass-1"], [Finding()]),
                new RunnerPriorFileResult("src/generated.cs", false, false, true, "matched an exclusion", null, null, [], []),
                new RunnerPriorFileResult("src/b.cs", false, true, false, null, "the model refused", null, [], []),
            ]);

        Assert.Equal(3, job.FileReviewResults.Count);

        var complete = job.FileReviewResults.Single(result => result.FilePath == "src/a.cs");
        Assert.True(complete.IsComplete);
        Assert.Equal("looks fine", complete.PerFileSummary);
        Assert.Equal(["pass-1"], complete.ReviewedPassKeys);
        Assert.Single(complete.Comments!);

        Assert.True(job.FileReviewResults.Single(result => result.FilePath == "src/generated.cs").IsExcluded);
        Assert.True(job.FileReviewResults.Single(result => result.FilePath == "src/b.cs").IsFailed);
    }

    // A carried-forward row must come back as one. Seeded as a freshly reviewed file, synthesis would
    // thread its old comments in as new candidates instead of suppressing them and labelling the file.
    [Fact]
    public void ACarriedForwardSeed_IsRebuiltAsCarriedForward()
    {
        var job = RunnerReviewSubject.BuildJob(RunnerManifests.Sample());

        RunnerReviewSubject.SeedPriorResults(
            job,
            [new RunnerPriorFileResult("src/kept.cs", true, false, false, null, null, "unchanged since baseline", [], [], IsCarriedForward: true)]);

        var row = Assert.Single(job.FileReviewResults);
        Assert.True(row.IsCarriedForward);
        Assert.True(row.IsComplete);
        Assert.Equal("unchanged since baseline", row.PerFileSummary);
    }

    private static ReviewComment Finding()
    {
        return new ReviewComment("src/a.cs", 1, CommentSeverity.Warning, "Bounded?");
    }

    private sealed class FakeWorkspace : IReviewRepositoryWorkspace
    {
        public IReadOnlyList<ChangedFileSummary> Changed { get; set; } = [];

        public Dictionary<string, string> Contents { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Diffs { get; } = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public ReviewRepositoryWorkspaceLease Lease { get; } = new(
            Guid.Empty, "key", "/mirror", "/source", "/target", "head", "base", "merge-base",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Active");

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct) =>
            Task.FromResult(this.Changed);

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
        {
            this.Reads.Add(path);
            return Task.FromResult(this.Contents.GetValueOrDefault(path));
        }

        public Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct) =>
            Task.FromResult(this.Diffs.GetValueOrDefault(path));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
