// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;

/// <summary>
///     Generate-only entry point for the PR-wide review strategy. Runs the plan -> investigate -> synthesize stages
///     over the whole change set on a caller-supplied model runtime and returns candidate findings tagged with
///     PR-wide provenance. This entry point performs NO disposition: it does not verify, gate, reconcile, publish,
///     or screen. The caller flows the returned candidates through the shared synthesis verify -> gate -> publish
///     path so a PR-wide pass meets per-file findings at the same per-finding gate.
/// </summary>
public interface IPrWideCandidateGenerator
{
    /// <summary>
    ///     Runs the bounded PR-wide generation stages once and returns the resulting candidate findings.
    /// </summary>
    /// <param name="job">The review job being executed.</param>
    /// <param name="pr">The pull request under review.</param>
    /// <param name="baseContext">The job-level review context (tools, prompts, protocol recorder).</param>
    /// <param name="runtime">
    ///     The resolved chat runtime for the pass's configured model. Generation binds to this runtime and never
    ///     falls back to the job default.
    /// </param>
    /// <param name="budget">Hard caps that bound the generation work.</param>
    /// <param name="unionPassIndex">
    ///     The 1-based pass index recorded on each candidate's provenance so a published finding renders "Pass N".
    ///     The per-file baseline is pass 1, so a job-level pass entry uses its list ordinal plus two.
    /// </param>
    /// <param name="shadow">
    ///     Whether this is a shadow pass. A shadow pass still runs and records its full generation trace plus a
    ///     completion event with its catch count, but the caller does not publish its candidates. The returned
    ///     candidates carry the shadow marker on their provenance.
    /// </param>
    /// <param name="reasoningEffort">
    ///     Reasoning effort configured for this pass, applied to the outbound chat requests unconditionally.
    ///     <see cref="ReviewReasoningEffort.None" /> leaves the request without an effort level (current behavior).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<CandidateReviewFinding>> GenerateCandidatesAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IResolvedAiChatRuntime runtime,
        PrWideGenerationBudget budget,
        int unionPassIndex,
        bool shadow,
        ReviewReasoningEffort reasoningEffort,
        CancellationToken ct);
}
