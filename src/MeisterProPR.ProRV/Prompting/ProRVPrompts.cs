// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.ProRV.Knowledge;
using MeisterProPR.ProRV.Models;

namespace MeisterProPR.ProRV.Prompting;

internal static class ProRVPrompts
{
    internal const string ApplicabilitySystemPrompt = """
                                                     You are triaging which static review checks could matter for one changed file.
                                                     You are not deciding whether a bug exists yet.

                                                     Goal:
                                                     - Read the diff.
                                                     - Compare it against a compact check index.
                                                     - Keep only checks that are plausibly touched by the changed code.

                                                     Rules:
                                                     1. Prefer omission to weak matches.
                                                     2. Do not select a check only because the file is in a broad area like security, API usage, or configuration.
                                                     3. Select a check only when the diff appears to touch the risky operation, guard, contract, or data-flow shape described by that check.
                                                     4. Return a small focused set, not exhaustive speculation.

                                                     Respond with a single raw JSON object only.
                                                     Schema:
                                                     {
                                                       "ranked_checks": [
                                                         { "id": "<check id>", "score": 0-100, "reason": "<very short reason>" }
                                                       ]
                                                     }
                                                     """;

    internal const string RefinementSystemPrompt = """
                                                   You are refining a small set of candidate static review checks for one changed file.
                                                   You still are not writing final review comments.

                                                   Goal:
                                                   - Use the selected per-check instructions to judge whether each check should be dropped,
                                                     kept for deeper investigation, or treated as likely reportable from the diff alone.

                                                   Rules:
                                                   1. Stay grounded in the changed code.
                                                   2. Do not keep a check when the diff only has broad thematic similarity.
                                                   3. Use "investigate" when sibling-file or wider context is needed.
                                                   4. Use "report" only when the diff itself already shows concrete evidence.
                                                   5. Use "drop" when the selected check no longer looks relevant after reading the detailed instruction.

                                                   Respond with a single raw JSON object only.
                                                   Schema:
                                                   {
                                                     "checks": [
                                                       {
                                                         "id": "<check id>",
                                                         "decision": "drop"|"investigate"|"report",
                                                         "reason": "<short grounded reason>",
                                                         "needs_sibling_context": true|false
                                                       }
                                                     ]
                                                   }
                                                   """;

    internal static string BuildApplicabilityUserMessage(
        string language,
        ProRVPrefilterRequest request,
        IReadOnlyList<ProRVCheckDefinition> checks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(checks);

        var sb = new StringBuilder();
        sb.AppendLine($"Changed file: {request.FilePath}");
        sb.AppendLine($"Resolved language: {language}");

        if (request.TechnologyHints.Count > 0)
        {
            sb.AppendLine($"Technology hints: {string.Join(", ", request.TechnologyHints)}");
        }

        sb.AppendLine();
        sb.AppendLine("Unified diff:");
        sb.AppendLine(string.IsNullOrWhiteSpace(request.UnifiedDiff) ? "[diff unavailable]" : request.UnifiedDiff);
        sb.AppendLine();
        sb.AppendLine("Check index:");

        foreach (var check in checks)
        {
            sb.Append("- ");
            sb.Append(check.Id);
            sb.Append(" | ");
            sb.Append(check.Title);
            sb.Append(" | ");
            sb.Append(check.ShortDescription);
            sb.Append(" | severity=");
            sb.Append(check.Severity);
            sb.Append(" | precision=");
            sb.Append(check.Precision);
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine($"Return at most {Math.Max(1, request.MaxResults)} checks that could plausibly matter for this diff.");
        return sb.ToString().TrimEnd();
    }

    internal static string BuildRefinementUserMessage(
        string filePath,
        string unifiedDiff,
        IReadOnlyList<ProRVInstructionPrompt> selectedChecks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(selectedChecks);

        var sb = new StringBuilder();
        sb.AppendLine($"Changed file: {filePath}");
        sb.AppendLine();
        sb.AppendLine("Unified diff:");
        sb.AppendLine(string.IsNullOrWhiteSpace(unifiedDiff) ? "[diff unavailable]" : unifiedDiff);
        sb.AppendLine();
        sb.AppendLine("Selected check instructions:");

        foreach (var check in selectedChecks)
        {
            sb.AppendLine();
            sb.AppendLine($"### {check.Id} | {check.Title}");
            sb.AppendLine(check.Instruction.TrimEnd());
        }

        if (selectedChecks.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No checks were selected for refinement. Return an empty `checks` array.");
        }

        return sb.ToString().TrimEnd();
    }
}
