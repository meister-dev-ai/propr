// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Verdict kinds of the evidence-backed judge, as recorded in <see cref="VerificationOutcome.JudgeVerdict" />.
/// </summary>
public static class EvidenceJudgeVerdicts
{
    /// <summary>The finding is true and the behaviour is not shown to be intended. The finding is published.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>The finding is not true, or the code shown does not exhibit it. The finding is withheld.</summary>
    public const string NotConfirmed = "not_confirmed";

    /// <summary>The finding is true, the behaviour is stated as intended, and it contradicts nothing. The finding is withheld.</summary>
    public const string Intended = "intended";

    /// <summary>
    ///     The finding is true and the behaviour is stated as intended, but it contradicts a named source such as other
    ///     functionality, a linked work item or the pull request description. The finding is published.
    /// </summary>
    public const string IntendedContradicted = "intended_contradicted";

    /// <summary>
    ///     The judge response was not a usable verdict: not JSON, an unknown verdict, or a contradiction verdict that
    ///     names no source or only a placeholder such as "none". The finding is withheld.
    /// </summary>
    public const string Unparseable = "unparseable";
}
