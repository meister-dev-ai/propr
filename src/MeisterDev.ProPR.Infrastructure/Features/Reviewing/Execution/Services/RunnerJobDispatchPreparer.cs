// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;

/// <summary>
///     Turns a job whose lease a runner just won into something the runner can actually execute: a mirror
///     this replica can serve, and the branch and revision facts the manifest names.
///     <para>
///         The mirror is registered against the job on this replica, which is why a runner fetches from the
///         replica that granted its lease and no other. That is a deliberate consequence of the mirror being
///         a path on local disk rather than a shared artifact: making it shared would mean a second storage
///         system to secure, size, and evict.
///     </para>
/// </summary>
public sealed partial class RunnerJobDispatchPreparer(
    IReviewRepositoryWorkspaceManager workspaces,
    IRunnerWorkspaceRegistry registry,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRunnerJobToolsRegistry toolsRegistry,
    IPullRequestFetcher? pullRequests,
    IOptions<ReviewWorkspaceOptions> workspaceOptions,
    IProCursorGateway? proCursorGateway = null,
    ReviewJobReuse? reuse = null,
    IRepositoryExclusionFetcher? exclusionFetcher = null,
    IReviewFileResultStore? priorRows = null,
    ILogger<RunnerJobDispatchPreparer>? logger = null) : IRunnerJobDispatchPreparer
{
    /// <inheritdoc />
    public async Task<RunnerJobDispatchPreparation> PrepareAsync(
        ReviewJob job,
        ReviewJobLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(lease);

        var branches = await this.ResolveBranchesAsync(job, ct);
        if (branches is null)
        {
            return RunnerJobDispatchPreparation.Failed("The pull request's branch names could not be resolved, so no workspace could be prepared.");
        }

        var (sourceBranch, targetBranch) = branches.Value;

        // A job that never had a revision resolved cannot be dispatched anywhere, in-process or remote.
        // Saying so here returns it to the queue rather than handing a runner a manifest it cannot use.
        if (job.ReviewRevisionReference is null)
        {
            return RunnerJobDispatchPreparation.Failed("The job has no resolved review revision, so no workspace can be prepared for it.");
        }

        var preparation = await workspaces.PrepareAsync(
            new ReviewRepositoryWorkspaceRequest(
                job.Id,
                job.ClientId,
                job.Provider,
                job.OrganizationUrl,
                job.CodeReviewReference.Repository,
                job.PullRequestId,
                job.ReviewRevisionReference,
                sourceBranch,
                targetBranch),
            ct);

        if (!preparation.Succeeded)
        {
            return RunnerJobDispatchPreparation.Failed(preparation.Failure?.Message ?? "The repository workspace could not be prepared.");
        }

        var workspace = preparation.Workspace!;
        var workspaceLease = workspace.Lease;
        var maxTransferBytes = (long)workspaceOptions.Value.MaxCacheSizeMegabytes * 1024 * 1024;

        // Registered before the manifest is handed out, so the fetch path is ready the moment the runner
        // has a reason to use it. The other order leaves a window where a fast runner asks for content the
        // replica has not admitted it holds. The registry takes ownership of the workspace here: it must
        // outlive this call — the runner fetches from the mirror throughout its execution — and it must
        // still be disposed when the job leaves this replica, or its checkouts stay on disk forever.
        await registry.RegisterAsync(
            job.Id,
            new RunnerWorkspaceSource(
                workspaceLease.MirrorPath,
                workspaceLease.HeadSha,
                workspaceLease.BaseSha,
                maxTransferBytes),
            workspace);

        var changedPaths = await workspace.GetChangedFilesAsync(ct);

        // What this job may adopt from the reviews before it, written to its rows before the manifest
        // leaves: the executor's prior-results read returns them, so a resumed or superseding-iteration
        // job leased to a runner neither re-pays finished work nor synthesizes over a different set than
        // the in-process path would have.
        await this.AdoptPriorWorkAsync(job, workspaceLease, targetBranch, changedPaths, ct);

        var conversation = await this.ReadConversationAsync(job, ct);

        // The tools the runner reaches back through, built here from the same factory and the same request
        // the in-process path uses. Registered rather than merely constructed: the proxy answers a call by
        // finding the job's tools on this replica, so tools that exist but were never registered are a
        // surface that refuses everything — and refuses it as a lost lease, which is not what happened.
        toolsRegistry.Register(
            job.Id,
            reviewContextToolsFactory.Create(
                new ReviewContextToolsRequest(
                    job.CodeReviewReference,
                    sourceBranch,
                    job.IterationId,
                    job.ClientId,
                    job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                        ? job.ProCursorSourceIds
                        : null,
                    job.OrganizationUrl,
                    targetBranch,
                    [.. changedPaths.Select(ChangedPathSnapshot.FromChangedFileSummary)],
                    Workspace: workspace,
                    WorkspaceLease: workspaceLease,
                    WorkspaceFailure: preparation.Failure)),

            // The same fact the in-process path gates its ProCursor tools on. Read from the gateway rather
            // than from the tools object, whose own answer is internal to the review assembly; a host with
            // no gateway at all is treated as not offering them, because claiming a surface that cannot
            // answer is the failure this is here to avoid.
            proCursorGateway?.IsConfigured ?? false);

        return RunnerJobDispatchPreparation.Ready(
            new RunnerJobManifestRequest(
                job,
                lease,
                targetBranch,
                [.. changedPaths.Select(f => f.Path)],
                $"runners/execution/workspace/{job.Id:D}/{lease.Generation}",
                maxTransferBytes,
                conversation?.Description,
                conversation?.ExistingThreads,
                conversation));
    }

    /// <summary>
    ///     Applies the same adopt-prior-work rules the in-process path applies at review start: resume from
    ///     a prior attempt at this revision, carry forward from the previous iteration's baseline. Writes
    ///     the adopted rows onto this job so the executor reads them back through prior-results.
    ///     <para>
    ///         Fail-soft in the one place the two paths cannot be identical: the in-process path
    ///         delta-scopes a full-coverage baseline through the provider, and this path computes the same
    ///         delta from the mirror. A delta the mirror cannot answer falls back to the partial-baseline
    ///         rule — the same fallback the in-process path uses when the provider's compare handle is
    ///         unusable — rather than guessing.
    ///     </para>
    /// </summary>
    private async Task AdoptPriorWorkAsync(
        ReviewJob job,
        ReviewRepositoryWorkspaceLease workspaceLease,
        string targetBranch,
        IReadOnlyList<ChangedFileSummary> changedPaths,
        CancellationToken ct)
    {
        if (reuse is null || priorRows is null)
        {
            return;
        }

        // Adoption happens once. A job re-dispatched after a lost claim or a returned lease already has
        // its adopted rows (or its own progress), and writing them again would violate the one-row-per-file
        // invariant the store enforces.
        var existing = await priorRows.GetByIdWithFileResultsAsync(job.Id, ct);
        if (existing is null || existing.FileReviewResults.Count > 0)
        {
            return;
        }

        var state = await reuse.LoadScanStateAsync(job, ct);
        if (state.ResumeJob is null && state.BaselineJob is null)
        {
            return;
        }

        var changedPathsSet = new HashSet<string>(changedPaths.Select(file => file.Path), StringComparer.OrdinalIgnoreCase);
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await reuse.ResumePriorFileResultsAsync(job, state.ResumeJob, changedPathsSet, claimedPaths, ct);

        if (state.BaselineJob is null)
        {
            return;
        }

        var carrySet = changedPathsSet;
        var fullCoverage = false;
        if (state.BaselineIsFullCoverage)
        {
            var delta = await this.TryComputeDeltaSinceBaselineAsync(
                workspaceLease.MirrorPath,
                state.BaselineJob.RevisionHeadSha,
                job.RevisionHeadSha,
                ct);
            if (delta is not null)
            {
                carrySet = delta;
                fullCoverage = true;
            }
        }

        var exclusionRules = await this.FetchExclusionRulesAsync(job, targetBranch, ct);
        await reuse.CarryForwardBaselineResultsAsync(job, state.BaselineJob, fullCoverage, carrySet, exclusionRules, claimedPaths, ct);
    }

    /// <summary>
    ///     The files changed between the baseline's head and this revision's head, from the mirror the
    ///     workspace was just prepared from — the provider-neutral version of the compare the in-process
    ///     fetch asks the provider for. Null when the mirror cannot answer, which the caller treats as the
    ///     partial-baseline fallback.
    /// </summary>
    private async Task<HashSet<string>?> TryComputeDeltaSinceBaselineAsync(
        string mirrorPath,
        string? baselineHeadSha,
        string? currentHeadSha,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baselineHeadSha) || string.IsNullOrWhiteSpace(currentHeadSha))
        {
            return null;
        }

        try
        {
            var git = new Workspace.GitCommandRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Workspace.GitCommandRunner>.Instance);

            // quotePath off, or any path with a byte outside ASCII comes back C-quoted — "src/caf\303\251.cs",
            // quotes included — matches nothing in the stored rows, and a file that DID change is carried
            // forward with the previous iteration's comments instead of being reviewed.
            var diff = await git.RunAsync(
                mirrorPath,
                ["-c", "core.quotePath=false", "diff", "--name-only", baselineHeadSha, currentHeadSha],
                null,
                ct);

            if (diff.ExitCode != 0)
            {
                LogDeltaUnavailable(logger, baselineHeadSha, diff.StandardError.Trim());
                return null;
            }

            return new HashSet<string>(
                diff.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDeltaUnavailable(logger, baselineHeadSha, ex.Message);
            return null;
        }
    }

    /// <summary>Fail-soft, like the in-process fetch: no rules beats no dispatch.</summary>
    private async Task<ReviewExclusionRules> FetchExclusionRulesAsync(ReviewJob job, string targetBranch, CancellationToken ct)
    {
        if (exclusionFetcher is null)
        {
            return ReviewExclusionRules.Default;
        }

        try
        {
            return await exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                targetBranch,
                job.ClientId,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogExclusionRulesUnavailable(logger, job.Id, ex.Message);
            return ReviewExclusionRules.Default;
        }
    }

    private static void LogDeltaUnavailable(ILogger? logger, string baselineHeadSha, string reason)
    {
        logger?.LogInformation(
            "The mirror could not answer the delta since baseline {BaselineHeadSha}; carrying forward under the partial-baseline rule instead: {Reason}",
            baselineHeadSha,
            reason);
    }

    private static void LogExclusionRulesUnavailable(ILogger? logger, Guid jobId, string reason)
    {
        logger?.LogWarning(
            "Exclusion rules could not be fetched while preparing job {JobId} for dispatch; using defaults: {Reason}",
            jobId,
            reason);
    }

    /// <summary>
    ///     The review's description and the conversation already on it, both of which the reviewer reads and
    ///     neither of which an executor can fetch for itself.
    ///     <para>
    ///         Deliberately the metadata-only fetch: pulling every changed file's content here to learn a
    ///         description is the request profile behind a large-review overload, and the executor gets the
    ///         content from the mirror anyway.
    ///     </para>
    ///     <para>
    ///         Fail-soft. A conversation that cannot be read costs the review its duplicate suppression,
    ///         which is worse than having it and better than refusing to dispatch the job at all.
    ///     </para>
    /// </summary>
    private async Task<PullRequest?> ReadConversationAsync(ReviewJob job, CancellationToken ct)
    {
        if (pullRequests is null)
        {
            return null;
        }

        try
        {
            return await pullRequests.FetchThreadContextAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                job.ClientId,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The branch names, from the job when intake already recorded them and from the provider when it
    ///     did not. Preferring the recorded pair keeps dispatch off the provider's rate limit for the common
    ///     case, and a job old enough to predate the recording still dispatches rather than failing.
    /// </summary>
    private async Task<(string Source, string Target)?> ResolveBranchesAsync(ReviewJob job, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(job.PrSourceBranch) && !string.IsNullOrWhiteSpace(job.PrTargetBranch))
        {
            return (job.PrSourceBranch!, job.PrTargetBranch!);
        }

        if (pullRequests is null)
        {
            return null;
        }

        var reference = await pullRequests.FetchRefAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.ClientId,
            ct);

        return string.IsNullOrWhiteSpace(reference.SourceBranch) || string.IsNullOrWhiteSpace(reference.TargetBranch)
            ? null
            : (reference.SourceBranch, reference.TargetBranch);
    }
}
