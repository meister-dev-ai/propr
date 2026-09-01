// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Finalization check that forecasts the two finding classes authors dismiss without acting: findings whose
///     target is a generated file (lockfiles, checksums, build outputs — the author's answer is "regenerate"), and
///     findings that flag a deliberately skipped test as a coverage gap (the author wrote the skip and knows).
///     The check is observe-only: it records a dismiss forecast as a protocol observation and leaves every
///     decision unchanged, so publication behavior is identical and the forecasts can be scored against the
///     client's real dismissal stream before any of them is allowed to act.
/// </summary>
public sealed partial class AcceptanceForecastCheck : IFindingFinalizationCheck
{
    /// <summary>Stable name of this check, surfaced in protocol observations.</summary>
    public const string CheckName = "acceptance_forecast_deterministic";

    /// <summary>Observation outcome recorded for a finding the check expects the author to dismiss.</summary>
    public const string DismissForecastOutcome = "dismiss_forecast";

    // Path shapes of files whose content is produced by tools rather than written by the author. A finding on
    // such a file is answered by regenerating, not by editing, so authors close it without acting on the text.
    [GeneratedRegex(
        @"(^|/)(go\.(work\.)?sum|package-lock\.json|yarn\.lock|pnpm-lock\.yaml|Cargo\.lock|composer\.lock|Gemfile\.lock|poetry\.lock|uv\.lock)$|\.(min\.js|min\.css|generated\.cs|g\.cs|designer\.cs)$|(^|/)(__generated__|node_modules)(/|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedFilePattern();

    // A finding of the skipped-test class describes the skip in its own message; the anchor file being a test
    // file confines the match. This reads the finding's claim, not the repository, so the check stays
    // deterministic and side-effect free at the cost of missing skip findings that avoid the word.
    [GeneratedRegex(@"\bskip(ped|ping|s)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SkipWordPattern();

    [GeneratedRegex(@"(^|/)((test|spec)s?[_./-]|.*[_.](test|spec)s?\.)", RegexOptions.IgnoreCase)]
    private static partial Regex TestFilePattern();

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public FinalizationCheckOutcome Evaluate(CandidateReviewFinding finding, FinalGateDecision currentDecision)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(currentDecision);

        // Only findings that would publish are worth forecasting; anything the gate already held back never
        // reaches the author.
        if (!string.Equals(currentDecision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
            || string.IsNullOrEmpty(finding.FilePath))
        {
            return FinalizationCheckOutcome.Unchanged(currentDecision);
        }

        if (GeneratedFilePattern().IsMatch(finding.FilePath))
        {
            return new FinalizationCheckOutcome(
                currentDecision,
                new FinalizationObservation(
                    CheckName,
                    finding.FindingId,
                    DismissForecastOutcome,
                    "targets a generated file; the expected author response is to regenerate, not edit"));
        }

        if (TestFilePattern().IsMatch(finding.FilePath) && SkipWordPattern().IsMatch(finding.Message))
        {
            return new FinalizationCheckOutcome(
                currentDecision,
                new FinalizationObservation(
                    CheckName,
                    finding.FindingId,
                    DismissForecastOutcome,
                    "flags a skipped test as a gap; a skip the author wrote is usually answered with \"intentional\""));
        }

        return FinalizationCheckOutcome.Unchanged(currentDecision);
    }
}
