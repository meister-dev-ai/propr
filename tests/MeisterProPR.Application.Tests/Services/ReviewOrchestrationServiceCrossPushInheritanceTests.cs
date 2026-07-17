// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Tests.Services;

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

        var sut = CreateService(store, fetcher, reviewExecutor, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

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

        public Task<PullRequestRef> FetchRefAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId,
            Guid? clientId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PullRequestRef("feature/x", "main", PrStatus.Active));
        }

        public Task<PullRequest> FetchAsync(
            string organizationUrl, string projectId, string repositoryId, int pullRequestId, int iterationId,
            int? compareToIterationId = null, Guid? clientId = null, CancellationToken cancellationToken = default,
            ReviewRevision? compareToReviewRevision = null, IReviewRepositoryWorkspace? workspace = null)
        {
            var isDelta = compareToIterationId.HasValue || compareToReviewRevision is not null;
            this.LastFetchWasDelta = isDelta;

            var changed = (isDelta ? deltaPaths : fullPaths)
                .Select(p => new ChangedFile(p, ChangeType.Edit, $"content {p}", $"diff {p}"))
                .ToList();
            var fullManifest = isDelta
                ? fullPaths.Select(p => new ChangedFileSummary(p, ChangeType.Edit)).ToList()
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

        public IReadOnlyList<string> ReviewedFor(Guid jobId)
        {
            return this._reviewedByJob.TryGetValue(jobId, out var paths) ? paths : [];
        }

        public async Task<ReviewResult> ReviewAsync(
            ReviewJob job, PullRequest pr, ReviewSystemContext baseContext, CancellationToken ct, IChatClient? overrideClient = null)
        {
            var jobWithResults = await repository.GetByIdWithFileResultsAsync(job.Id, ct) ?? job;
            var existing = jobWithResults.FileReviewResults.ToDictionary(r => r.FilePath, StringComparer.Ordinal);

            var selection = ReviewFileSelectionService.SelectFilesForReview(pr.ChangedFiles, existing, baseContext.ExclusionRules);

            var reviewed = new List<string>();
            foreach (var file in selection.FilesToReview)
            {
                var result = new ReviewFileResult(job.Id, file.Path);
                result.MarkCompleted($"reviewed {file.Path}", []);
                await repository.AddFileResultAsync(result, ct);
                reviewed.Add(file.Path);
            }

            this._reviewedByJob[job.Id] = reviewed;
            return new ReviewResult("Summary", []);
        }
    }
}
