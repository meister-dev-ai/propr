// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     End-to-end offline verification (no live model) that cross-push review inheritance works when the whole
///     <see cref="ReviewOrchestrationService" /> pipeline runs against the real in-memory job repository — so the
///     baseline is genuinely selected from persisted job history and carry-forward is genuinely persisted, rather
///     than being stubbed. Exercises both the Azure DevOps (iteration-id) and non-ADO (revision) provider paths.
/// </summary>
public partial class ReviewOrchestrationServiceTests
{
    private const string CrossPushOrg = "https://scm.example.com/org";
    private const string CrossPushProject = "proj";
    private const string CrossPushRepo = "repo";
    private const int CrossPushPrId = 4242;

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.AzureDevOps)]
    public async Task CrossPush_CompletedBaseline_CarriesForwardUnchanged_ReviewsOnlyDelta(ScmProvider provider)
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);

        // PR of three files; a later push changes only B.cs.
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);

        var sut = CreateService(store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // First push (revision one): review the whole PR to completion.
        var firstJob = MakeCrossPushJob(provider, clientId, iteration: 1, revisionToken: "one");
        SetupReviewerIdReturns(clientRegistry, firstJob, Guid.NewGuid());
        await repo.AddAsync(firstJob);
        await sut.ProcessAsync(firstJob, CancellationToken.None);

        var firstReloaded = repo.GetById(firstJob.Id);
        Assert.NotNull(firstReloaded);
        Assert.Equal(JobStatus.Completed, firstReloaded!.Status);
        Assert.Equal(
            new[] { "src/A.cs", "src/B.cs", "src/C.cs" },
            reviewExecutor.ReviewedFor(firstJob.Id).OrderBy(p => p, StringComparer.Ordinal));

        // Second push (revision two): a new job for the same PR at a different revision.
        var secondJob = MakeCrossPushJob(provider, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        var addResult = await repo.TryAddIfNoActiveDuplicateAsync(secondJob);
        Assert.True(addResult.WasAdded);

        await sut.ProcessAsync(secondJob, CancellationToken.None);

        // The full-coverage completed baseline is delta-scoped: the fetcher was asked for the delta only.
        Assert.True(fetcher.LastFetchWasDelta);

        var second = await repo.GetByIdWithFileResultsAsync(secondJob.Id);
        Assert.NotNull(second);
        var carriedForward = CarriedForwardPaths(second!);
        var reviewedFresh = reviewExecutor.ReviewedFor(secondJob.Id);

        // Unchanged files inherit; only the changed delta is re-reviewed — NOT the whole PR.
        Assert.Equal(new[] { "src/A.cs", "src/C.cs" }, carriedForward.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(new[] { "src/B.cs" }, reviewedFresh.OrderBy(p => p, StringComparer.Ordinal));
        Assert.DoesNotContain("src/A.cs", reviewedFresh);
        Assert.DoesNotContain("src/C.cs", reviewedFresh);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.AzureDevOps)]
    public async Task CrossPush_SupersededPartialBaseline_ReviewsGapFiles_AndCarriesForwardReviewedUnchanged(ScmProvider provider)
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);

        // PR of three files; the later push changes only B.cs.
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);

        var sut = CreateService(
            store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger,
            workspaceManager: CreateWorkspaceManager(PathsChangedSinceTheFirstPush("src/B.cs")));

        // First push (revision one) started reviewing but only finished A.cs before the next push arrived:
        // a partial baseline (reviewed 1 of 3 in-scope files) still in Processing.
        var partialJob = MakeCrossPushJob(provider, clientId, iteration: 1, revisionToken: "one");
        await repo.AddAsync(partialJob);
        await repo.TryTransitionAsync(partialJob.Id, JobStatus.Pending, JobStatus.Processing);
        await repo.AddFileResultAsync(MakeCompletedResult(partialJob.Id, "src/A.cs"));
        await repo.UpdateInScopeChangedFileCountAsync(partialJob.Id, 3);

        // Second push (revision two) supersedes the in-flight partial job.
        var secondJob = MakeCrossPushJob(provider, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        var addResult = await repo.TryAddIfNoActiveDuplicateAsync(secondJob);
        Assert.True(addResult.WasAdded);
        Assert.Equal(1, addResult.CancelledSupersededJobCount);
        Assert.Equal(JobStatus.Superseded, repo.GetById(partialJob.Id)!.Status);

        await sut.ProcessAsync(secondJob, CancellationToken.None);

        // A partial baseline forces a full-PR fetch so never-reviewed files are not skipped.
        Assert.False(fetcher.LastFetchWasDelta);

        var second = await repo.GetByIdWithFileResultsAsync(secondJob.Id);
        Assert.NotNull(second);
        var carriedForward = CarriedForwardPaths(second!);
        var reviewedFresh = reviewExecutor.ReviewedFor(secondJob.Id);

        // The baseline's one reviewed-and-unchanged file inherits...
        Assert.Equal(new[] { "src/A.cs" }, carriedForward.OrderBy(p => p, StringComparer.Ordinal));
        // ...the changed delta (B.cs) AND the unchanged-but-never-reviewed file (C.cs) are both reviewed fresh
        // (the coverage invariant), while the inherited file is not re-reviewed.
        Assert.Equal(new[] { "src/B.cs", "src/C.cs" }, reviewedFresh.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Contains("src/C.cs", reviewedFresh);
        Assert.DoesNotContain("src/A.cs", reviewedFresh);
    }

    // A file review that failed at the first push leaves a failed row and no result. The later push does not
    // change the file, so the delta does not contain it and carry-forward has nothing to copy. Without the
    // file joining the delta fetch, it was neither reviewed nor reported on any later push.
    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task CrossPush_CompletedBaselineWithAFailedFileReview_ReviewsTheUnchangedFileAgain(ScmProvider provider)
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);

        // PR of three files; a later push changes only B.cs.
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);

        var sut = CreateService(store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // First push: the review of C.cs fails, the review of the other files completes, and so does the job.
        reviewExecutor.FailingPaths.Add("src/C.cs");
        var firstJob = MakeCrossPushJob(provider, clientId, iteration: 1, revisionToken: "one");
        SetupReviewerIdReturns(clientRegistry, firstJob, Guid.NewGuid());
        await repo.AddAsync(firstJob);
        await sut.ProcessAsync(firstJob, CancellationToken.None);

        var first = await repo.GetByIdWithFileResultsAsync(firstJob.Id);
        Assert.Equal(JobStatus.Completed, first!.Status);
        Assert.True(first.FileReviewResults.Single(row => row.FilePath == "src/C.cs").IsFailed);

        // Second push: C.cs did not change, and its review now succeeds.
        reviewExecutor.FailingPaths.Clear();
        var secondJob = MakeCrossPushJob(provider, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(secondJob)).WasAdded);

        await sut.ProcessAsync(secondJob, CancellationToken.None);

        // The fetch stays delta-scoped and adds the failed file to the files it fetches.
        Assert.True(fetcher.LastFetchWasDelta);
        Assert.Equal(new[] { "src/C.cs" }, fetcher.LastAdditionalFilePaths);

        var second = await repo.GetByIdWithFileResultsAsync(secondJob.Id);
        Assert.Equal(new[] { "src/A.cs" }, CarriedForwardPaths(second!));
        Assert.Equal(
            new[] { "src/B.cs", "src/C.cs" },
            reviewExecutor.ReviewedFor(secondJob.Id).OrderBy(p => p, StringComparer.Ordinal));
        Assert.False(second!.FileReviewResults.Single(row => row.FilePath == "src/C.cs").IsFailed);
    }

    // A file whose review keeps failing is tried again on every push, and each review records the failure again,
    // so the summary of each review lists the file as failed.
    [Fact]
    public async Task CrossPush_AFileWhoseReviewKeepsFailing_IsTriedAgainOnEveryPush()
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);
        reviewExecutor.FailingPaths.Add("src/C.cs");
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);
        var sut = CreateService(store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        var jobs = new List<ReviewJob>();
        foreach (var (iteration, token) in new[] { (1, "one"), (2, "two"), (3, "three") })
        {
            var job = MakeCrossPushJob(ScmProvider.GitHub, clientId, iteration, token);
            SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
            Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(job)).WasAdded);
            await sut.ProcessAsync(job, CancellationToken.None);
            jobs.Add(job);
        }

        foreach (var job in jobs)
        {
            Assert.Contains("src/C.cs", reviewExecutor.AttemptedFor(job.Id));
            var reloaded = await repo.GetByIdWithFileResultsAsync(job.Id);
            Assert.Equal(JobStatus.Completed, reloaded!.Status);
            Assert.True(reloaded.FileReviewResults.Single(row => row.FilePath == "src/C.cs").IsFailed);
        }
    }

    // A failed file that the pull request no longer contains is not fetched again.
    [Fact]
    public async Task CrossPush_AFailedFileThatLeftThePullRequest_IsNotReviewed()
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);
        var sut = CreateService(store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        reviewExecutor.FailingPaths.Add("src/C.cs");
        var firstJob = MakeCrossPushJob(ScmProvider.GitHub, clientId, iteration: 1, revisionToken: "one");
        SetupReviewerIdReturns(clientRegistry, firstJob, Guid.NewGuid());
        await repo.AddAsync(firstJob);
        await sut.ProcessAsync(firstJob, CancellationToken.None);

        // The second push removes C.cs from the pull request.
        reviewExecutor.FailingPaths.Clear();
        fetcher.FullPaths = ["src/A.cs", "src/B.cs"];
        var secondJob = MakeCrossPushJob(ScmProvider.GitHub, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(secondJob)).WasAdded);
        await sut.ProcessAsync(secondJob, CancellationToken.None);

        Assert.Equal(new[] { "src/B.cs" }, reviewExecutor.ReviewedFor(secondJob.Id));
    }

    // Azure DevOps anchors inline comments through the iteration comparison of the fetch, which does not contain
    // an unchanged file. When a failed file joins the review, the comments are anchored to the full diff, which
    // contains every reviewed file. A review without such a file keeps the comparison.
    [Fact]
    public async Task CrossPush_AzureDevOps_AnchorsToTheFullDiffOnlyWhenAFailedFileJoinsTheReview()
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);
        var sut = CreateService(store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        reviewExecutor.FailingPaths.Add("src/C.cs");
        var firstJob = MakeCrossPushJob(ScmProvider.AzureDevOps, clientId, iteration: 1, revisionToken: "one");
        SetupReviewerIdReturns(clientRegistry, firstJob, Guid.NewGuid());
        await repo.AddAsync(firstJob);
        await sut.ProcessAsync(firstJob, CancellationToken.None);

        // Second push: it changes A.cs and B.cs, C.cs joins the review, and its review succeeds. With three
        // reviewed files, the second review becomes the baseline of the third.
        reviewExecutor.FailingPaths.Clear();
        fetcher.DeltaPaths = ["src/A.cs", "src/B.cs"];
        var secondJob = MakeCrossPushJob(ScmProvider.AzureDevOps, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(secondJob)).WasAdded);
        await sut.ProcessAsync(secondJob, CancellationToken.None);

        // Third push: no file review failed in the baseline, so nothing joins the delta.
        var thirdJob = MakeCrossPushJob(ScmProvider.AzureDevOps, clientId, iteration: 3, revisionToken: "three");
        SetupReviewerIdReturns(clientRegistry, thirdJob, Guid.NewGuid());
        Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(thirdJob)).WasAdded);
        await sut.ProcessAsync(thirdJob, CancellationToken.None);

        var compareIterations = PublishedCompareIterations(commentPoster);
        Assert.Equal(new int?[] { null, null, 2 }, compareIterations);
    }

    private static IReadOnlyList<int?> PublishedCompareIterations(ICodeReviewPublicationService commentPoster)
    {
        return commentPoster.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ICodeReviewPublicationService.PublishReviewAsync))
            .Select(call => call.GetArguments()[6] as ReviewPublicationContext)
            .Select(context => (context?.ProviderSpecificContext as AzureDevOpsPublicationContext)?.CompareToIterationId)
            .ToList();
    }

    // The partial baseline reviewed B.cs before the next push changed it. The review of the next push fetches the
    // whole pull request, and B.cs keeps no result of its previous content: it is reviewed again, while A.cs, which
    // did not change, carries forward.
    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.AzureDevOps)]
    public async Task CrossPush_SupersededPartialBaseline_ReviewsAReviewedFileThatChangedAfterIt(ScmProvider provider)
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);
        var sut = CreateService(
            store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger,
            workspaceManager: CreateWorkspaceManager(PathsChangedSinceTheFirstPush("src/B.cs")));

        var partialJob = await StartPartialReviewAsync(repo, provider, clientId, "src/A.cs", "src/B.cs");
        var secondJob = MakeCrossPushJob(provider, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(secondJob)).WasAdded);
        Assert.Equal(JobStatus.Superseded, repo.GetById(partialJob.Id)!.Status);

        await sut.ProcessAsync(secondJob, CancellationToken.None);

        Assert.False(fetcher.LastFetchWasDelta);
        var second = await repo.GetByIdWithFileResultsAsync(secondJob.Id);
        Assert.Equal(new[] { "src/A.cs" }, CarriedForwardPaths(second!));
        Assert.Equal(
            new[] { "src/B.cs", "src/C.cs" },
            reviewExecutor.ReviewedFor(secondJob.Id).OrderBy(p => p, StringComparer.Ordinal));
    }

    // When the mirror cannot compare the head of the partial baseline with the reviewed head, no reviewed file can
    // be shown to be unchanged, and the review covers every file of the pull request.
    [Fact]
    public async Task CrossPush_SupersededPartialBaseline_WithoutAComparison_ReviewsEveryFile()
    {
        var (_, _, _, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var clientId = Guid.NewGuid();
        var repo = new InMemoryReviewJobRepository();
        var store = new ReviewJobExecutionStoreAdapter(repo);
        var reviewExecutor = new RecordingReviewOrchestrator(repo);
        var fetcher = new ScriptedPullRequestFetcher(
            fullPaths: ["src/A.cs", "src/B.cs", "src/C.cs"],
            deltaPaths: ["src/B.cs"]);
        var sut = CreateService(
            store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger,
            workspaceManager: CreateWorkspaceManager(null));

        await StartPartialReviewAsync(repo, ScmProvider.GitHub, clientId, "src/A.cs");
        var secondJob = MakeCrossPushJob(ScmProvider.GitHub, clientId, iteration: 2, revisionToken: "two");
        SetupReviewerIdReturns(clientRegistry, secondJob, Guid.NewGuid());
        Assert.True((await repo.TryAddIfNoActiveDuplicateAsync(secondJob)).WasAdded);

        await sut.ProcessAsync(secondJob, CancellationToken.None);

        var second = await repo.GetByIdWithFileResultsAsync(secondJob.Id);
        Assert.Empty(CarriedForwardPaths(second!));
        Assert.Equal(
            new[] { "src/A.cs", "src/B.cs", "src/C.cs" },
            reviewExecutor.ReviewedFor(secondJob.Id).OrderBy(p => p, StringComparer.Ordinal));
    }

    // A review at revision one that completed the given files and was still running when the next push arrived.
    private static async Task<ReviewJob> StartPartialReviewAsync(
        InMemoryReviewJobRepository repo, ScmProvider provider, Guid clientId, params string[] reviewedPaths)
    {
        var partialJob = MakeCrossPushJob(provider, clientId, iteration: 1, revisionToken: "one");
        await repo.AddAsync(partialJob);
        await repo.TryTransitionAsync(partialJob.Id, JobStatus.Pending, JobStatus.Processing);
        foreach (var path in reviewedPaths)
        {
            await repo.AddFileResultAsync(MakeCompletedResult(partialJob.Id, path));
        }

        await repo.UpdateInScopeChangedFileCountAsync(partialJob.Id, 3);
        return partialJob;
    }

    private static IReadOnlySet<string> PathsChangedSinceTheFirstPush(params string[] paths)
    {
        return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    }

    // A workspace whose mirror answers the comparison with the head of a baseline with the given paths, or cannot
    // compare the two revisions when they are null.
    private static IReviewRepositoryWorkspaceManager CreateWorkspaceManager(IReadOnlySet<string>? pathsChangedSinceBaseline)
    {
        var workspace = Substitute.For<IReviewRepositoryWorkspace>();
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);
        workspace.GetPathsChangedSinceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(pathsChangedSinceBaseline));
        var manager = Substitute.For<IReviewRepositoryWorkspaceManager>();
        manager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));
        return manager;
    }

    private static ReviewJob MakeCrossPushJob(ScmProvider provider, Guid clientId, int iteration, string revisionToken)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId, CrossPushOrg, CrossPushProject, CrossPushRepo, CrossPushPrId, iteration);

        if (provider == ScmProvider.AzureDevOps)
        {
            // Azure DevOps keys the revision on the numeric iteration id carried in ProviderRevisionId.
            job.SetReviewRevision(
                new ReviewRevision($"head-{revisionToken}", "base", null, iteration.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
        }
        else
        {
            job.SetProviderReviewContext(
                new CodeReviewRef(
                    new RepositoryRef(new ProviderHostRef(provider, "https://scm.example.com"), CrossPushRepo, "org", "org/repo"),
                    CodeReviewPlatformKind.PullRequest,
                    CrossPushPrId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CrossPushPrId));
            // Non-ADO keys the revision on the head SHA (surfaced through ProviderRevisionId).
            job.SetReviewRevision(new ReviewRevision($"sha-{revisionToken}", "base", null, $"sha-{revisionToken}", $"base...sha-{revisionToken}"));
        }

        return job;
    }

    private static ReviewFileResult MakeCompletedResult(Guid jobId, string path)
    {
        var result = new ReviewFileResult(jobId, path);
        result.MarkCompleted($"prior summary for {path}", []);
        return result;
    }

    private static IReadOnlyList<string> CarriedForwardPaths(ReviewJob job)
    {
        return job.FileReviewResults.Where(r => r.IsCarriedForward).Select(r => r.FilePath).ToList();
    }

    /// <summary>
    ///     A pull-request fetcher whose changed-file set depends on whether a delta compare handle was supplied:
    ///     full PR when none, delta-only (with the full manifest attached) when either the ADO iteration id or the
    ///     provider-neutral compare revision is passed. Records what the last fetch scoped to.
    /// </summary>
    private sealed class ScriptedPullRequestFetcher(IReadOnlyList<string> fullPaths, IReadOnlyList<string> deltaPaths)
        : IPullRequestFetcher
    {
        public bool LastFetchWasDelta { get; private set; }

        public IReadOnlyList<string> LastAdditionalFilePaths { get; private set; } = [];

        public IReadOnlyList<string> FullPaths { get; set; } = fullPaths;

        public IReadOnlyList<string> DeltaPaths { get; set; } = deltaPaths;

        public Task<PullRequestRef> FetchRefAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId,
            Guid? clientId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PullRequestRef("feature/x", "main", PrStatus.Active));
        }

        public Task<PullRequest> FetchAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId, int iterationId,
            int? compareToIterationId = null, Guid? clientId = null, CancellationToken cancellationToken = default,
            ReviewRevision? compareToReviewRevision = null, IReviewRepositoryWorkspace? workspace = null,
            IReadOnlyCollection<string>? additionalFilePaths = null)
        {
            var isDelta = compareToIterationId.HasValue || compareToReviewRevision is not null;
            this.LastFetchWasDelta = isDelta;
            this.LastAdditionalFilePaths = [.. additionalFilePaths ?? []];

            // Like the provider fetchers: a delta fetch adds the requested files the full listing contains.
            var scope = isDelta
                ? this.DeltaPaths.Where(this.FullPaths.Contains)
                    .Concat(this.FullPaths.Where(p => !this.DeltaPaths.Contains(p) && (additionalFilePaths?.Contains(p) ?? false)))
                : this.FullPaths;
            var changed = scope
                .Select(p => new ChangedFile(p, ChangeType.Edit, $"content {p}", $"diff {p}"))
                .ToList();
            var fullManifest = isDelta
                ? this.FullPaths.Select(p => new ChangedFileSummary(p, ChangeType.Edit)).ToList()
                : null;

            return Task.FromResult(
                new PullRequest(
                    organizationUrl, projectId, repositoryId, repositoryId, pullRequestId, iterationId,
                    "Cross-push PR", null, "feature/x", "main", changed,
                    PrStatus.Active, AllChangedFileSummaries: fullManifest));
        }

        public Task<ChangedFile?> FetchFileDiffAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId, int iterationId,
            string filePath, int? compareToIterationId = null, Guid? clientId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ChangedFile?>(null);
        }

        public Task<IReadOnlyList<PrCommentThread>> FetchThreadsAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId,
            Guid? clientId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<PrCommentThread>>([]);
        }

        public Task<PullRequest> FetchThreadContextAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId, int iterationId,
            Guid? clientId = null, CancellationToken cancellationToken = default,
            bool includeChangedFileManifest = false)
        {
            return this.FetchAsync(
                organizationUrl,
                projectId,
                repositoryId,
                pullRequestId,
                iterationId,
                null,
                clientId,
                cancellationToken);
        }
    }

    /// <summary>
    ///     A stand-in for the AI review executor that performs no model calls: it selects the files still needing
    ///     review with the real <see cref="ReviewFileSelectionService" /> (so already-persisted carried-forward
    ///     results are skipped exactly as in production), persists a completed result for each, and records the set
    ///     it reviewed per job so tests can assert what was reviewed fresh versus inherited.
    /// </summary>
    private sealed class RecordingReviewOrchestrator(IJobRepository repository) : IFileByFileReviewOrchestrator
    {
        private readonly Dictionary<Guid, List<string>> _reviewedByJob = [];
        private readonly Dictionary<Guid, List<string>> _attemptedByJob = [];

        /// <summary>
        ///     Paths whose review fails. The stand-in records a failed row for each and completes the review of the
        ///     other files, which is the state a job ends in when file reviews still fail in its last attempt.
        /// </summary>
        public HashSet<string> FailingPaths { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> ReviewedFor(Guid jobId)
        {
            return this._reviewedByJob.TryGetValue(jobId, out var paths) ? paths : [];
        }

        public IReadOnlyList<string> AttemptedFor(Guid jobId)
        {
            return this._attemptedByJob.TryGetValue(jobId, out var paths) ? paths : [];
        }

        public async Task<ReviewResult> ReviewAsync(
            ReviewJob job, PullRequest pr, ReviewSystemContext baseContext, CancellationToken ct, IChatClient? overrideClient = null)
        {
            var jobWithResults = await repository.GetByIdWithFileResultsAsync(job.Id, ct) ?? job;
            var existing = jobWithResults.FileReviewResults.ToDictionary(r => r.FilePath, StringComparer.Ordinal);

            var selection = ReviewFileSelectionService.SelectFilesForReview(pr.ChangedFiles, existing, baseContext.ExclusionRules);

            var reviewed = new List<string>();
            var attempted = new List<string>();
            var failed = new List<string>();
            foreach (var file in selection.FilesToReview)
            {
                attempted.Add(file.Path);
                var result = new ReviewFileResult(job.Id, file.Path);
                if (this.FailingPaths.Contains(file.Path))
                {
                    result.MarkFailed("The model endpoint refused the request.");
                    failed.Add(file.Path);
                }
                else
                {
                    result.MarkCompleted($"reviewed {file.Path}", []);
                    reviewed.Add(file.Path);
                }

                await repository.AddFileResultAsync(result, ct);
            }

            this._reviewedByJob[job.Id] = reviewed;
            this._attemptedByJob[job.Id] = attempted;
            return new ReviewResult("Summary", []) { FailedReviewFilePaths = failed };
        }
    }
}
