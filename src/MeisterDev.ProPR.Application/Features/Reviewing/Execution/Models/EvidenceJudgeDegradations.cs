// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Reasons why the evidence-backed judge could not decide a claim, as recorded in
///     <see cref="VerificationOutcome.JudgeDegradation" />. A claim with one of these reasons is withheld.
/// </summary>
public static class EvidenceJudgeDegradations
{
    /// <summary>No judge model, no review tools or no anchor path was available, so the judge did not run.</summary>
    public const string Unavailable = "judge_unavailable";

    /// <summary>The anchor file could not be read, for example because the pull request deletes it.</summary>
    public const string AnchorUnreadable = "anchor_unreadable";

    /// <summary>The anchor read, the excerpt reads or another step around the judge call failed with an error.</summary>
    public const string Error = "judge_error";

    /// <summary>
    ///     The model provider failed the judge call: the call threw, the response carried a provider error such as an
    ///     overloaded server, or the response held no output and no token usage.
    /// </summary>
    public const string ProviderError = "judge_provider_error";

    /// <summary>The judge call did not finish within the per-call time limit.</summary>
    public const string Timeout = "judge_timeout";

    /// <summary>The judge answered, but the answer was not a usable verdict.</summary>
    public const string Unparseable = "judge_unparseable";
}
