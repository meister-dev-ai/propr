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

    /// <summary>
    ///     The provided code contradicts the claim, or the claimed mechanism is absent where the provided code would
    ///     show it. The finding is withheld.
    /// </summary>
    public const string NotConfirmed = "not_confirmed";

    /// <summary>
    ///     The mechanism may be real, but the code needed to decide is not in the provided inputs. A finding from a pass
    ///     that reviewed with repository tools is published, because that pass could read the code the judge does not
    ///     receive. A finding from a pass without repository tools is withheld.
    /// </summary>
    public const string InsufficientContext = "insufficient_context";

    /// <summary>
    ///     The finding is true, but it leads to no concrete wrong outcome, failure or maintainability defect that a
    ///     reviewer would ask the author to fix, or its conclusion does not follow from the mechanism. The finding is
    ///     withheld.
    /// </summary>
    public const string NotActionable = "not_actionable";

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
