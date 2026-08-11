// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Reassembles a submitted result and publishes it exactly once, through the path an in-process review
///     already uses.
///     <para>
///         The state lives in the ledger, not here: this service is scoped to one request, and both the
///         chunk assembly and the publish-once guard exist precisely to correlate calls that arrive on
///         different ones.
///     </para>
/// </summary>
public sealed class RunnerFindingsIntake(
    IRunnerCallAuthorizer authorizer,
    IReviewResultPublisher publisher,
    IRunnerJobBudgetRegistry budgets,
    IRunnerJobToolsRegistry tools,
    RunnerSubmissionLedger ledger,
    RunnerRelayReplayCache replays,
    IRunnerWorkspaceRegistry workspaces) : IRunnerFindingsIntake
{
    /// <inheritdoc />
    public async Task<RunnerSubmissionResult> SubmitAsync(
        RunnerCallContext call,
        RunnerFindingsChunk chunk,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.SubmissionId);

        if (chunk.ChunkCount < 1 || chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount)
        {
            return RunnerSubmissionResult.Rejected("The chunk index is outside the declared chunk count.");
        }

        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            // Covers the case the story cares about most: the same job submitted twice from two lease
            // generations, because the original executor came back after a reclaim. The older one is
            // refused here rather than allowed to publish a second review.
            return RunnerSubmissionResult.NotAuthorized(authorization.Refusal);
        }

        // A resend of a submission that already published. Answered as success and published nothing,
        // because the comments are already out and posting them again is the one thing that must not
        // happen twice.
        if (ledger.Published.TryGetValue(call.JobId, out var publishedSubmission))
        {
            return string.Equals(publishedSubmission, chunk.SubmissionId, StringComparison.Ordinal)
                ? RunnerSubmissionResult.AlreadyPublished()
                : RunnerSubmissionResult.Rejected(
                    "This job has already published a different submission; a second one would post a "
                    + "second review.");
        }

        var assembly = ledger.Assembling.GetOrAdd(
            call.JobId,
            _ => new RunnerSubmissionAssembly(chunk.SubmissionId, chunk.ChunkCount));

        if (!assembly.Accepts(chunk))
        {
            return RunnerSubmissionResult.Rejected("The chunk does not belong to the submission this job is assembling.");
        }

        assembly.Add(chunk);
        if (!assembly.IsComplete)
        {
            // Nothing is published from a partial payload. Publishing what has arrived so far would post
            // half a review and leave no way to tell that is what happened.
            return RunnerSubmissionResult.AwaitingChunks(assembly.Missing);
        }

        var result = assembly.Build();
        result = this.FillSoftCapFigures(call.JobId, result);

        // Claimed before publishing, so a resend arriving while this one is still posting is answered as
        // already published rather than starting a second post.
        if (!ledger.Published.TryAdd(call.JobId, chunk.SubmissionId))
        {
            return RunnerSubmissionResult.AlreadyPublished();
        }

        try
        {
            await publisher.PublishAsync(call.JobId, result, ct);
        }
        catch
        {
            // Publication failed, so the claim is given back: the executor may retry, and the job has not
            // in fact published. Leaving the claim would strand a review that never posted.
            ledger.Published.TryRemove(call.JobId, out _);
            throw;
        }

        ledger.Assembling.TryRemove(call.JobId, out _);

        // The job is over, so the control plane stops holding it open. Left registered, the scope, the
        // tools, the served completions, and the workspace's disk would outlive every job this replica
        // ever dispatched. The publish-once entry stays, so a resend still arriving under this lease is
        // answered rather than posted again.
        budgets.Release(call.JobId);
        tools.Release(call.JobId);
        replays.Release(call.JobId);
        await workspaces.ReleaseAsync(call.JobId);
        return RunnerSubmissionResult.Published();
    }

    /// <summary>
    ///     Fills in the soft-cap figures a remote review cannot know.
    ///     <para>
    ///         The executor sees only the verdict, because the wind-down signal carries no numbers: the
    ///         completions are priced here, against the job's budget scope. Persisted without figures, the
    ///         result never gets its budget block, and the paid resume that the budget block gates re-bills
    ///         the whole review instead of continuing it.
    ///     </para>
    /// </summary>
    private ReviewResult FillSoftCapFigures(Guid jobId, ReviewResult result)
    {
        if (!result.BudgetSoftCapped
            || (result.BudgetSoftCapThresholdUsd is not null && result.BudgetSoftCapSpentUsd is not null))
        {
            return result;
        }

        var breach = budgets.Find(jobId)?.IncrementSoftCapBreach;
        if (breach is null)
        {
            return result;
        }

        return result with
        {
            BudgetSoftCapThresholdUsd = result.BudgetSoftCapThresholdUsd ?? breach.ThresholdUsd,
            BudgetSoftCapSpentUsd = result.BudgetSoftCapSpentUsd ?? breach.SpentUsd,
        };
    }
}
