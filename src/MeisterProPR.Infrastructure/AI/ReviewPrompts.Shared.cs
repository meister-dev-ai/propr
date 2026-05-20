// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static partial class ReviewPrompts
{
    /// <summary>
    ///     Canonical per-file output-key reminder. Centralised here to prevent wording drift
    ///     across <see cref="SystemPrompt" />, <see cref="AgenticLoopGuidance" />, and
    ///     <see cref="BuildPerFileContextPrompt" />, which all enforce the same JSON contract.
    /// </summary>
    internal const string OutputKeyReminder =
        "CRITICAL OUTPUT RULE — REMINDER: Your final JSON response MUST use exactly these keys: " +
        "\"summary\" (string), \"comments\" (array), \"confidence_evaluations\" (array), " +
        "\"investigation_complete\" (bool), \"loop_complete\" (bool). " +
        "All review findings MUST go into the \"comments\" array as " +
        "{\"file_path\": \"...\", \"line_number\": <int|null>, \"severity\": \"info\"|\"warning\"|\"error\"|\"suggestion\", \"message\": \"...\"}. " +
        "Do NOT use key_issues, verdict, issues, review, suggested_changes, suggested_fixes, key_findings, or any other structure. " +
        "\"summary\" must be a plain string, NOT an array or object. " +
        "CRITICAL: Your response message strings MUST NOT contain any HTML tags (e.g., no <style>, <script>, <div>, <!DOCTYPE>, or any < or > characters). " +
        "If you need to show code with angle brackets, wrap it in markdown code fences (triple backticks) or escape it. " +
        "Never output raw HTML tags in comment messages.";

    /// <summary>
    ///     Fixed reviewer-persona primer. Output constraint aligns with <see cref="OutputKeyReminder" />.
    /// </summary>
    internal const string SystemPrompt = """
                                         You are an expert code reviewer specialising in general
                                         software engineering best practices. Review pull requests for bugs, security
                                         vulnerabilities, code quality issues, performance problems, and maintainability.

                                         CRITICAL OUTPUT RULE: Your ENTIRE response must be a single raw JSON object.
                                         Do NOT wrap it in markdown code fences (no ```json or ```). Do NOT add any
                                         text before or after the JSON. The very first character must be '{' and the
                                         very last character must be '}'. Any other format will cause a parse error.

                                         HTML SAFETY RULE: Your message strings MUST NOT contain any HTML tags or raw angle brackets.
                                         Never output <style>, <script>, <div>, <!DOCTYPE>, or any < or > characters outside of markdown code blocks.
                                         If you need to reference code with angle brackets, wrap the code in triple backticks (```code here```).

                                         MARKDOWN: Utilize markdown formatting to enhance clarity using bullet points, numbered lists, fenced code blocks, bold font etc.

                                         Schema:
                                         {
                                           "summary": "<overall narrative>",
                                           "comments": [
                                             { "file_path": "<relative path or null>", "line_number": <int or null>,
                                               "severity": "warning"|"error"|"suggestion", "message": "<text>" }
                                           ],
                                           "confidence_evaluations": [
                                             { "concern": "<area>", "confidence": <0-100> }
                                           ],
                                           "investigation_complete": true|false,
                                           "loop_complete": true|false
                                         }
                                         """;

    /// <summary>
    ///     Agentic loop guidance appended after <see cref="SystemPrompt" /> for all tool-aware reviews.
    ///     Describes available tools, review strategy, and the extended JSON schema that includes
    ///     <c>confidence_evaluations</c> and <c>loop_complete</c>. Output constraint aligns with
    ///     <see cref="OutputKeyReminder" />.
    ///     Rules are ordered by importance (primacy effect): highest-impact behavioural rules appear first.
    /// </summary>
    internal const string AgenticLoopGuidance = """
                                                CERTAINTY GATE — READ THIS FIRST, EVERY TIME:
                                                Before adding ANY entry to the "comments" array, pass this self-check:
                                                  1. Have I directly seen the problematic code — in the diff or via get_file_content?
                                                  2. Is the problem definite, not something I merely suspect might exist?
                                                  3. Does my comment name a specific observable (a line, token, pattern, call, or value)?
                                                If the answer to any of these is "no", discard the comment entirely.
                                                Do NOT soften it to a suggestion or downgrade its severity — discard it.
                                                Omission is always preferable to speculation.
                                                Phrases that mean a comment MUST be discarded:
                                                  "if your ", "if the file", "if [", "please verify", "validate that",
                                                  "consider whether", "this may be", "this could be", "you may want to",
                                                  "worth checking", "it appears", "it seems", "i cannot confirm",
                                                  "unclear whether", "worth verifying", "if applicable"

                                                INFO rule: The "comments" array MUST NOT contain any entry with severity "info".
                                                Observations, positive notes, and low-level informational items belong in the
                                                "summary" field only. If you want to highlight something positive or note a
                                                non-actionable observation, write it in the summary narrative. The "comments"
                                                array is for actionable findings only.

                                                HTML SAFETY RULE: Your message strings (both in the "summary" and in comment "message" fields)
                                                MUST NOT contain any HTML tags or raw angle brackets. This includes <style>, <script>, <div>,
                                                <!DOCTYPE>, or any standalone < or > characters. If you need to show code that contains angle brackets,
                                                always wrap it in markdown triple backticks (```code here```). This preserves the code formatting
                                                and prevents HTML injection in Azure DevOps. Do NOT output raw HTML tags.

                                                SUGGESTION rule: A SUGGESTION entry in the "comments" array is only valid if:
                                                  1. It names a specific, observable thing to change (not "consider refactoring this area").
                                                  2. It provides or implies a concrete alternative action (not "you might think about X").
                                                  3. It is grounded in something directly observable in the diff or fetched content.
                                                Suggestions phrased as "consider adding", "you could also", "you might also",
                                                "it would be worth", "could be strengthened", or "could also verify" are vague
                                                and MUST be omitted — do NOT batch them, do NOT downgrade them, omit them.

                                                Severity calibration: ERROR = you have confirmed the defect by reading the code.
                                                WARNING = you have evidence but the fix requires context to confirm.
                                                SUGGESTION = a concrete, specific, actionable improvement with a clear alternative.
                                                Your confidence_evaluations are enforced mechanically after each review:
                                                  confidence < 80 → severity is downgraded from ERROR to WARNING automatically.
                                                  confidence < 60 → severity is downgraded from WARNING to SUGGESTION automatically.
                                                Self-calibrate accordingly: do not file as ERROR if you are not at least 80% confident.

                                                Cross-file pattern consolidation rule: When the same concern (same root cause, same fix)
                                                applies to 3 or more files in the PR, do NOT post individual comment threads for each
                                                file. Instead, post one consolidated comment thread on the first affected file, listing
                                                all affected file paths. Once you have posted a consolidated thread for a pattern, do
                                                not re-post that pattern for any additional file.

                                                You have access to the following tools:
                                                - get_changed_files: Get the list of all files changed in this pull request, including paths and change types.
                                                - get_file_tree: Get the full file tree of the repository at a specified branch.
                                                - get_file_content: Get a range of lines from a file at a specific branch (1-based, inclusive). Always specify a reasonable line range such as 1-100.

                                                Review strategy:
                                                1. Review all diffs provided in the initial context.
                                                2. Use tools to fetch additional context when needed (e.g. full file content when only a partial diff is visible, or related files not in the diff).
                                                3. After each investigation step, assess your confidence in the review across the concerns you identified.

                                                CRITICAL OUTPUT RULE: Respond with a single raw JSON object ONLY.
                                                Do NOT use markdown code fences (no ```json or ```). Do NOT add any
                                                explanation before or after the JSON. The response must start with '{'
                                                and end with '}' — any other format will cause a hard parse failure.
                                                Schema:
                                                {
                                                  "summary": "<overall review narrative>",
                                                  "comments": [
                                                    { "file_path": "<relative path or null>", "line_number": <int or null>,
                                                      "severity": "warning"|"error"|"suggestion", "message": "<text>" }
                                                  ],
                                                  "confidence_evaluations": [
                                                    { "concern": "<area such as 'security', 'code_quality', 'architecture', 'correctness'>", "confidence": <0-100> }
                                                  ],
                                                  "investigation_complete": true|false,
                                                  "loop_complete": true|false
                                                }

                                                Set loop_complete to true when you have sufficient confidence in your review and no further investigation is needed.
                                                Set loop_complete to false when you intend to call tools to gather more context before finalising.
                                                Set investigation_complete to false if you have not yet called any tools to inspect related files and the PR contains multiple changed files.
                                                Set investigation_complete to true once you have gathered sufficient cross-file context.

                                                Branch rule: When calling get_file_content or get_file_tree, always use the PR source branch labelled "Source branch" in the per-file context header. Never use the target branch (main/master/develop) unless explicitly told to compare against the target. When reviewing a configuration file (tsconfig*.json, vite.config.*, *.conf, Dockerfile, docker-compose*.yml, package.json) that references or is referenced by another config, call get_file_tree to discover sibling configs, then call get_file_content on the relevant ones before commenting.

                                                Binary/non-text file rule: Do NOT post WARNING or ERROR comments on binary files, images, compiled outputs, or generated lock files (e.g., *.png, *.jpg, *.gif, *.dll, *.lock, package-lock.json). If you are unable to review a binary file and wish to note this, include a single note in the general summary only — do not file it as a file-specific comment thread.

                                                Tooling limitation rule: If get_file_content or get_file_tree returns truncated, empty, or malformed content, treat any finding based on that content as a tooling limitation, not a confirmed defect. Do NOT escalate a tooling-unreadable file to "warning" or "error". Note the limitation in the summary only.

                                                Spec/planning file rule: Files whose path matches /specs/, /docs/, plan.md, tasks.md, research.md, quickstart.md, data-model.md, or files under checklists/ or contracts/ are specification and planning artifacts — not implementation code. Apply a strict threshold: post at most 2 comment threads per such file, and only when the content contains a correctness-impacting gap (i.e., a developer implementing the spec literally would produce broken or incorrect behavior). Do NOT comment on editorial style, wording choices, traceability format, section structure, or documentation completeness for these files. For pure planning artifacts (plan.md, tasks.md, research.md, quickstart.md, data-model.md, checklists/, contracts/), post zero comment threads.

                                                Code Suggestion Block rule: When you are highly confident of a correct, minimal fix for a finding — and the fix is confined to a single file and a single contiguous hunk of code — you MAY include a fenced suggestion block immediately after the finding description in the relevant comment. The block must use the exact fence marker ```suggestion on its own line, followed by the complete corrected lines (not a diff), and closed with ``` on its own line. The fenced block MUST NOT be used for multi-file changes, architectural advice, prose-only recommendations, speculative improvements, or any fix that spans more than one contiguous hunk. Omit the suggestion block entirely when you are not highly confident of the exact fix.
                                                """;

    /// <summary>
    ///     Builds the system prompt for the agentic review loop, incorporating client-level
    ///     customisations and repository instructions when present.
    ///     Always starts with <see cref="SystemPrompt" /> (the fixed reviewer-persona primer)
    ///     followed by <see cref="AgenticLoopGuidance" /> (tools, extended schema, loop instructions).
    /// </summary>
    /// <param name="context">
    ///     Optional review system context containing the client system message and repository
    ///     instructions. When <see langword="null" />, only the base prompts are returned.
    /// </param>
    internal static string BuildSystemPrompt(ReviewSystemContext? context)
    {
        var sb = new StringBuilder();

        // Fixed reviewer-persona primer — always included regardless of client configuration.
        string baseSystemPrompt;
        if (context?.PromptOverrides.TryGetValue("SystemPrompt", out var overrideText) == true)
        {
            baseSystemPrompt = overrideText!;
        }
        else
        {
            baseSystemPrompt = PromptTemplateRuntime.RenderStage(PromptStageKeys.GlobalSystem);
        }

        sb.AppendLine(ComposePrompt(context, PromptStageKeys.GlobalSystem, PromptStageRole.System, baseSystemPrompt));
        sb.AppendLine();

        // Agentic loop guidance — always included for all tool-aware reviews.
        var agenticGuidance = context?.PromptOverrides.GetValueOrDefault("AgenticLoopGuidance") ?? AgenticLoopGuidance;
        sb.AppendLine(agenticGuidance);

        if (context is null)
        {
            return sb.ToString().TrimEnd();
        }

        if (!string.IsNullOrWhiteSpace(context.ClientSystemMessage))
        {
            sb.AppendLine();
            sb.AppendLine("## Client Instructions");
            sb.AppendLine(context.ClientSystemMessage);
        }

        if (context.RepositoryInstructions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Repository Instructions");
            sb.AppendLine("The following repository-specific instructions apply to this review:");

            foreach (var instruction in context.RepositoryInstructions)
            {
                sb.AppendLine();
                sb.AppendLine($"### {instruction.FileName}");
                sb.AppendLine($"**When to use:** {instruction.WhenToUse}");
                sb.AppendLine(instruction.Body);
            }
        }

        if (context.DismissedPatterns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Dismissed Patterns — Do Not Report");
            sb.AppendLine(
                "The following patterns have been dismissed by the client administrator. " +
                "Do NOT post any comment that matches or closely resembles these patterns. " +
                "Omit them entirely from the \"comments\" array:");
            foreach (var pattern in context.DismissedPatterns)
            {
                sb.AppendLine($"- {pattern}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Builds the stable "global" system message: persona + tools + client message + repository instructions.
    ///     This message is sent on iteration 1 only; subsequent iterations drop it from history to save tokens.
    /// </summary>
    internal static string BuildGlobalSystemPrompt(ReviewSystemContext? context)
    {
        return BuildSystemPrompt(context);
    }

    internal static string BuildUserMessage(PullRequest pr)
    {
        var sb = new StringBuilder();

        if (pr.ChangedFiles.Count == 0)
        {
            sb.AppendLine("No files changed. Return summary stating no changes found; empty comments array.");
            AppendExistingThreads(sb, pr);
            return sb.ToString();
        }

        sb.AppendLine($"Pull Request: {pr.Title}");
        sb.AppendLine($"{pr.SourceBranch} → {pr.TargetBranch}");
        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"Description: {pr.Description}");
        }

        sb.AppendLine();
        sb.AppendLine($"Changed Files ({pr.ChangedFiles.Count}):");

        foreach (var file in pr.ChangedFiles)
        {
            sb.AppendLine();
            if (file.IsBinary)
            {
                sb.AppendLine($"=== {file.Path} [{file.ChangeType}] === [binary file — content omitted]");
            }
            else
            {
                sb.AppendLine($"=== {file.Path} [{file.ChangeType}] ===");
                sb.AppendLine("======================================= FULL CONTENT =======================================");
                sb.AppendLine(file.FullContent);
                sb.AppendLine("======================================= END FULL CONTENT =======================================");
                sb.AppendLine("======================================= DIFF =======================================");
                sb.AppendLine(file.UnifiedDiff);
                sb.AppendLine("======================================= END DIFF =======================================");
            }
        }

        AppendExistingThreads(sb, pr);
        return sb.ToString();
    }

    private static void AppendExistingThreads(StringBuilder sb, PullRequest pr)
    {
        if (pr.ExistingThreads?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Existing Review Threads");
            sb.AppendLine(
                "The following threads already exist on this PR. Take them into account: " +
                "avoid re-flagging resolved issues, and consider developer responses.");

            foreach (var thread in pr.ExistingThreads)
            {
                var location = thread.FilePath is not null
                    ? $"{thread.FilePath}{(thread.LineNumber.HasValue ? $":L{thread.LineNumber}" : string.Empty)}"
                    : "(PR-level)";

                sb.AppendLine();
                sb.AppendLine($"### Thread at {location}");
                foreach (var comment in thread.Comments)
                {
                    sb.AppendLine($"  [{comment.AuthorName}]: {comment.Content}");
                }
            }
        }
    }

    /// <summary>
    ///     System prompt for the cross-file quality-filter AI pass (IMP-08).
    ///     Instructs the model to discard low-quality comments and return the survivors as JSON.
    /// </summary>
    internal static string BuildQualityFilterSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("QualityFilterSystemPrompt", out var overrideText) == true)
        {
            return overrideText!;
        }

        return """
               You are a senior code review editor. You will receive a set of code review comments in a
               markdown table. Your task is to remove low-quality comments and return only those that are
               actionable, specific, and well-grounded.

               Apply the following rules:

               1. DISCARD any comment that uses speculative language such as:
                  "if your", "please verify", "if this", "if applicable", "consider adding",
                  "you might also", "you could also", "it would be worth", "could also verify".

               2. DISCARD any comment that is a duplicate or near-duplicate of another comment
                  (same file, same concern stated differently).

               3. DISCARD any comment at ERROR severity where the message contains hedging language
                  or cannot be confirmed from the visible diff alone.

               4. DISCARD INFO-severity comments entirely — they belong in the summary, not in comments.

               5. DISCARD SUGGESTION comments that do not provide a concrete, observable alternative action.

               Respond ONLY with a JSON object in this exact format — no markdown fences, no explanation:
               {
                 "comments": [
                   { "file_path": "<path or null>", "line_number": <int or null>,
                     "severity": "warning"|"error"|"suggestion", "message": "<text>" }
                 ]
               }
               """;
    }

    /// <summary>
    ///     Builds the user message for the cross-file quality-filter AI pass.
    ///     Formats <paramref name="comments" /> as a numbered markdown table.
    /// </summary>
    internal static string BuildQualityFilterUserMessage(IReadOnlyList<ReviewComment> comments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review the following comments and apply the quality-filter rules:");
        sb.AppendLine();
        sb.AppendLine("| # | File | Line | Severity | Message |");
        sb.AppendLine("|---|------|------|----------|---------|");

        for (var i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            var file = c.FilePath ?? "(none)";
            var line = c.LineNumber?.ToString() ?? "-";
            var sev = c.Severity.ToString().ToLowerInvariant();
            var msg = c.Message.Replace("|", @"\|");
            sb.AppendLine($"| {i + 1} | {file} | {line} | {sev} | {msg} |");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     System prompt for bounded PR-level verification of synthesized cross-file findings.
    ///     The verifier must only promote findings when the provided evidence independently supports them.
    /// </summary>
    internal static string BuildPrVerificationSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrVerificationSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.PrVerificationSystem, PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage(PromptStageKeys.PrVerificationSystem);

        return ComposePrompt(context, PromptStageKeys.PrVerificationSystem, PromptStageRole.System, defaultText);
    }

    /// <summary>
    ///     User message for bounded PR-level verification.
    /// </summary>
    internal static string BuildPrVerificationUserMessage(ClaimDescriptor claim, EvidenceBundle evidence, ReviewSystemContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(evidence);

        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.PrVerificationUser,
            new PromptTemplateModels.PrVerificationUserModel(
                claim.ClaimId,
                claim.FindingId,
                claim.ClaimKind,
                claim.ClaimFamily,
                claim.AssertionText,
                evidence.CoverageState,
                evidence.HasProCursorAttempt,
                evidence.ProCursorResultStatus,
                evidence.RetrievalNotes,
                evidence.EvidenceItems.Count > 0,
                evidence.EvidenceItems.Select(item => new PromptTemplateModels.PromptEvidenceItemModel(
                    item.Kind,
                    item.SourceId,
                    item.Summary,
                    item.PayloadReference)).ToList(),
                evidence.EvidenceAttempts.Count > 0,
                evidence.EvidenceAttempts.Select(attempt => new PromptTemplateModels.PromptEvidenceAttemptModel(
                    attempt.SourceFamily,
                    attempt.Status,
                    attempt.CoverageImpact,
                    attempt.ScopeSummary,
                    attempt.FailureReason)).ToList()));

        return ComposePrompt(context, PromptStageKeys.PrVerificationUser, PromptStageRole.User, defaultText);
    }

    private static string ComposePrompt(
        ReviewSystemContext? context,
        string stageKey,
        PromptStageRole promptRole,
        string defaultText)
    {
        if (context?.PromptExperiment?.TryGetVariant(stageKey, promptRole, out var variant) != true || variant is null)
        {
            return defaultText;
        }

        return variant.CompositionMode switch
        {
            PromptCompositionMode.Replace => variant.Content,
            PromptCompositionMode.Prepend => string.Concat(variant.Content, Environment.NewLine, Environment.NewLine, defaultText),
            PromptCompositionMode.Append => string.Concat(defaultText, Environment.NewLine, Environment.NewLine, variant.Content),
            _ => defaultText,
        };
    }

    /// <summary>
    ///     System prompt for the memory-augmented reconsideration step (US3, feature 026).
    ///     Instructs the AI to review draft findings in light of historical resolved threads.
    /// </summary>
    internal static string BuildMemoryReconsiderationSystemPrompt(string reviewerIdentity)
    {
        return $"""
                You are {reviewerIdentity}, an expert code reviewer with access to historical memory of past PR review decisions.
                You are in the RECONSIDERATION phase — you will be given draft findings from an initial review pass
                alongside records of how similar issues were resolved previously in this codebase.

                Your task: evaluate each draft finding against the historical context and decide whether to:
                  - RETAIN: The current finding is valid even considering past resolutions (same problem recurs unfixed, or a different instance).
                  - DOWNGRADE: Lower the severity if history shows the team typically accepts this pattern.
                  - DISCARD: Remove the finding if a past resolution clearly demonstrates the same concern was intentionally accepted or by design.

                CRITICAL OUTPUT RULE: Your ENTIRE response must be a single raw JSON object using exactly these keys:
                  "summary" (string), "comments" (array with file_path/line_number/severity/message),
                  "confidence_evaluations" (array), "investigation_complete" (bool), "loop_complete" (bool).
                Do NOT wrap in markdown fences. Return only valid JSON.
                """;
    }

    /// <summary>
    ///     User message for the memory-augmented reconsideration step.
    ///     Combines draft findings JSON with formatted historical matches.
    /// </summary>
    internal static string BuildMemoryReconsiderationUserMessage(
        string draftFindingsJson,
        IReadOnlyList<ThreadMemoryMatchDto> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Draft Findings from Initial Review");
        sb.AppendLine(draftFindingsJson);
        sb.AppendLine();
        sb.AppendLine("## Historical Memory — Past Resolved Threads");

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            sb.AppendLine($"### Match {i + 1} (Similarity: {m.SimilarityScore:F2}, Memory ID: {m.MemoryRecordId})");
            if (m.FilePath is not null)
            {
                sb.AppendLine($"- **File**: {m.FilePath}");
            }

            sb.AppendLine($"- **How it was resolved**: {m.ResolutionSummary}");
            sb.AppendLine();
        }

        sb.AppendLine("Reconsider the draft findings above using the historical context. Return your reconsidered findings as a JSON object.");
        return sb.ToString();
    }
}
