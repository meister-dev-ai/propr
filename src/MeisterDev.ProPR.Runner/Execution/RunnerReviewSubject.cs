// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Rebuilds what a review is of, from the manifest and the fetched working copy.
///     <para>
///         In the control plane both the job and the pull request come out of the database and the provider
///         API. Neither is reachable here, so the manifest supplies the facts and the two worktrees supply
///         the content. The result has to be the same shape the in-process path builds, because the pipeline
///         downstream cannot tell — and must not be able to tell — which side assembled it.
///     </para>
/// </summary>
internal static class RunnerReviewSubject
{
    /// <summary>
    ///     The job the pipeline persists against, seeded from the manifest and never written to a database.
    /// </summary>
    /// <param name="manifest">The manifest describing the review.</param>
    public static ReviewJob BuildJob(RunnerJobManifest manifest)
    {
        var target = manifest.Target;
        var job = new ReviewJob(
            manifest.JobId,
            manifest.ClientId,
            target.OrganizationUrl,
            target.ProjectId,
            target.RepositoryId,
            target.Number,
            target.IterationId);

        job.SetProviderReviewContext(
            new CodeReviewRef(
                new RepositoryRef(
                    new ProviderHostRef(ParseProvider(target.Provider), target.OrganizationUrl),
                    target.RepositoryId,
                    target.ProjectId,
                    target.ProjectId,
                    target.RepositoryName),
                CodeReviewPlatformKind.PullRequest,
                target.ExternalReviewId,
                target.Number));

        job.SetReviewRevision(new ReviewRevision(target.HeadSha, target.BaseSha, null, target.ExternalReviewId, $"{target.BaseSha}...{target.HeadSha}"));
        job.SetPrContext(target.Title, target.RepositoryName, target.SourceBranch, target.TargetBranch);
        job.SetAiConfig(null, manifest.DefaultModel.RemoteModelId, manifest.Behaviour?.Temperature);
        // The pipeline reads the profile off the job, not the context, so a manifest that carries one has
        // to land it here — left unset, every remote review silently ran the Balanced profile whatever the
        // client configured.
        job.SetReviewPipelineProfile(manifest.Behaviour?.ReviewPipelineProfileId);
        job.Status = JobStatus.Processing;
        return job;
    }

    /// <summary>
    ///     Puts back what an earlier attempt at this job already recorded.
    ///     <para>
    ///         The pipeline decides what to review, and what to synthesize over, from the job's file
    ///         results. Seeded empty, a reclaimed review re-pays for every file and then publishes only what
    ///         its own attempt happened to see — the earlier findings are in the control plane's database
    ///         and nowhere in the review that is about to be posted.
    ///     </para>
    /// </summary>
    /// <param name="job">The in-memory job.</param>
    /// <param name="recorded">What the control plane has for it.</param>
    public static void SeedPriorResults(ReviewJob job, IReadOnlyList<RunnerPriorFileResult> recorded)
    {
        foreach (var seed in recorded)
        {
            var result = new ReviewFileResult(job.Id, seed.FilePath);

            // Order matters: a result can be both complete and excluded on the wire, and the entity refuses
            // to be marked twice. Exclusion is the stronger statement — an excluded file was never reviewed.
            if (seed.IsExcluded)
            {
                result.MarkExcluded(seed.ExclusionReason ?? "excluded");
            }
            else if (seed.IsFailed)
            {
                result.MarkFailed(seed.ErrorMessage ?? "The earlier attempt failed on this file.");
            }
            else if (seed.IsComplete && seed.IsCarriedForward)
            {
                // Rebuilt through the same factory the control plane used to write it, so synthesis sees a
                // carried-forward row — suppressing its candidates and labelling the file — instead of a
                // freshly reviewed one that happens to have old comments.
                result.MarkCompleted(seed.PerFileSummary ?? string.Empty, seed.Comments, seed.ReviewedPassKeys);
                result = ReviewFileResult.CreateCarriedForward(job.Id, result);
            }
            else if (seed.IsComplete)
            {
                result.MarkCompleted(seed.PerFileSummary ?? string.Empty, seed.Comments, seed.ReviewedPassKeys);
            }

            job.FileReviewResults.Add(result);
        }
    }

    /// <summary>
    ///     The pull request the reviewer reads: the manifest's description of it, plus one changed file per
    ///     path in the frozen scope, read out of the two worktrees.
    ///     <para>
    ///         The scope comes from the manifest rather than from a fresh diff. Recomputing it here would
    ///         let a push that landed after dispatch change what the review is of, which is the thing
    ///         freezing the scope exists to prevent.
    ///     </para>
    /// </summary>
    /// <param name="manifest">The manifest describing the review.</param>
    /// <param name="workspace">The fetched working copy.</param>
    /// <param name="ct">The cancellation token.</param>
    public static async Task<PullRequest> BuildPullRequestAsync(
        RunnerJobManifest manifest,
        IReviewRepositoryWorkspace workspace,
        CancellationToken ct)
    {
        var target = manifest.Target;
        var scope = new HashSet<string>(target.ChangedPaths, StringComparer.Ordinal);
        var summaries = await workspace.GetChangedFilesAsync(ct);

        var files = new List<ChangedFile>(scope.Count);
        foreach (var summary in summaries.Where(summary => scope.Contains(summary.Path)))
        {
            files.Add(await ReadChangedFileAsync(workspace, summary, ct));
        }

        return new PullRequest(
            target.OrganizationUrl,
            target.ProjectId,
            target.RepositoryId,
            target.RepositoryName,
            target.Number,
            target.IterationId,
            target.Title,
            target.Description,
            target.SourceBranch,
            target.TargetBranch,
            files,
            PrStatus.Active,
            [.. target.ExistingThreads.Select(ToThread)],
            [.. summaries],
            // Discovered and bounded at dispatch; the prompt reads them from the pull request exactly as
            // in-process. Left null when the manifest carries none, so the prompt section stays absent
            // rather than rendering an empty list.
            LinkedItems: manifest.LinkedItems is { Count: > 0 } linked
                ?
                [
                    .. linked.Select(item => new LinkedItem(
                        item.ProviderKey,
                        item.ItemType,
                        item.Title,
                        item.Description,
                        item.Url,
                        [.. item.RelatedLinks.Select(link => new LinkedItemRef(link.Kind, link.TargetKey, link.Url, link.Title))]))
                ]
                : null);
    }

    private static async Task<ChangedFile> ReadChangedFileAsync(
        IReviewRepositoryWorkspace workspace,
        ChangedFileSummary summary,
        CancellationToken ct)
    {
        // A deleted file has no head content to read, and reading it anyway would put an empty string where
        // the reviewer expects the file that was removed.
        var content = summary.ChangeType == ChangeType.Delete
            ? string.Empty
            : await workspace.ReadFileAsync(summary.Path, "source", ct) ?? string.Empty;

        var diff = await workspace.GetUnifiedDiffAsync(summary.Path, ct) ?? string.Empty;
        return new ChangedFile(summary.Path, summary.ChangeType, content, diff);
    }

    private static PrCommentThread ToThread(RunnerReviewThread thread)
    {
        return new PrCommentThread(
            null,
            thread.FilePath,
            thread.LineNumber,
            [.. thread.Comments.Select(comment => new PrThreadComment(comment.AuthorName ?? string.Empty, comment.Content))],
            thread.Status);
    }

    private static ScmProvider ParseProvider(string provider)
    {
        return Enum.TryParse<ScmProvider>(provider, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"The manifest names source-control provider '{provider}', which this runner build does not know. "
                + "The control plane is newer than the runner.");
    }
}
