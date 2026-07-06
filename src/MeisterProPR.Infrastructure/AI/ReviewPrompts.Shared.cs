// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterProPR.Infrastructure.AI;

internal static partial class ReviewPrompts
{
    internal static string OutputKeyReminder => PromptTemplateRuntime.ReadSharedPartial("output-key-reminder").Trim();

    internal static string SystemPrompt => PromptTemplateRuntime.ReadSharedPartial("system-prompt").Trim();

    internal static string AgenticLoopGuidance => PromptTemplateRuntime.ReadSharedPartial("agentic-loop-guidance").Trim();

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
        string baseSystemPrompt;
        if (context?.PromptOverrides.TryGetValue("SystemPrompt", out var overrideText) == true)
        {
            baseSystemPrompt = overrideText!;
        }
        else
        {
            var assertiveCertaintyGate = context?.Aggressiveness == ReviewAggressiveness.Assertive;
            // Pre-render the agentic loop guidance with the assertiveCertaintyGate flag so that
            // {{#if assertiveCertaintyGate}} blocks in agentic-loop-guidance.hbs are resolved.
            // The override path (if set) bypasses this and injects the override text directly.
            string agenticGuidance;
            if (context?.PromptOverrides.TryGetValue("AgenticLoopGuidance", out var overrideGuidance) == true)
            {
                agenticGuidance = overrideGuidance!;
            }
            else
            {
                agenticGuidance = PromptTemplateRuntime.RenderAgenticLoopGuidance(assertiveCertaintyGate);
            }

            baseSystemPrompt = PromptTemplateRuntime.RenderStage(
                PromptStageKeys.GlobalSystem,
                new PromptTemplateModels.GlobalSystemModel(
                    agenticGuidance,
                    !string.IsNullOrWhiteSpace(context?.ClientSystemMessage),
                    context?.ClientSystemMessage,
                    context is { RepositoryInstructions.Count: > 0 },
                    context?.RepositoryInstructions.Select(instruction => new PromptTemplateModels.PromptRepositoryInstructionModel(
                        instruction.FileName,
                        instruction.WhenToUse,
                        instruction.Body)).ToList() ?? [],
                    context is { DismissedPatterns.Count: > 0 },
                    context?.DismissedPatterns ?? [],
                    assertiveCertaintyGate));
        }

        return ComposePrompt(context, PromptStageKeys.GlobalSystem, PromptStageRole.System, baseSystemPrompt);
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
        return PromptTemplateRuntime.RenderStage(
            "legacy_pr_review_user",
            new PromptTemplateModels.LegacyPrReviewUserModel(
                pr.ChangedFiles.Count > 0,
                pr.Title,
                pr.SourceBranch,
                pr.TargetBranch,
                pr.Description,
                pr.ChangedFiles.Count,
                pr.ChangedFiles.Select(file => new PromptTemplateModels.PromptLegacyChangedFileModel(
                    file.Path,
                    file.ChangeType.ToString(),
                    file.IsBinary,
                    file.IsBinary ? null : file.FullContent,
                    file.IsBinary ? null : file.UnifiedDiff)).ToList(),
                pr.ExistingThreads?.Count > 0,
                pr.ExistingThreads?.Select(thread => new PromptTemplateModels.PromptThreadModel(
                        FormatThreadLocation(thread),
                        thread.Comments.Select(comment => new PromptTemplateModels.PromptThreadCommentModel(comment.AuthorName, comment.Content)).ToList()))
                    .ToList() ?? []));
    }

    /// <summary>
    ///     System prompt for the cross-file quality-filter AI pass (IMP-08).
    ///     Instructs the model to discard low-quality comments and return the survivors as JSON.
    ///     When <paramref name="context" /> has <see cref="ReviewAggressiveness.Assertive" /> posture,
    ///     rules 1 and 3 are relaxed to demote rather than discard.
    /// </summary>
    internal static string BuildQualityFilterSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("QualityFilterSystemPrompt", out var overrideText) == true)
        {
            return overrideText!;
        }

        var assertive = context?.Aggressiveness == ReviewAggressiveness.Assertive;
        return PromptTemplateRuntime.RenderStage("quality_filter_system", new PromptTemplateModels.QualityFilterSystemModel(assertive));
    }

    /// <summary>
    ///     System prompt for the per-file LLM self-reflection importance-ranking pass.
    /// </summary>
    internal static string BuildImportanceRankingSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("ImportanceRankingSystemPrompt", out var overrideText) == true)
        {
            return overrideText!;
        }

        return PromptTemplateRuntime.RenderStage("importance_ranking_system", new PromptTemplateModels.ImportanceRankingSystemModel());
    }

    /// <summary>
    ///     User message for the per-file LLM self-reflection importance-ranking pass.
    ///     Formats each candidate comment with its severity, deterministic score, hedging flag, and message text.
    /// </summary>
    internal static string BuildImportanceRankingUserMessage(IReadOnlyList<ReviewComment> comments, AiReviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(options);

        // The hedging hint was a phrase-list feature; language-robust screening replaces phrase matching with a
        // semantic (embedding) detector, so no phrase list is consulted here. The ranker no longer receives a
        // hedging signal (the model field stays for template compatibility and is a follow-up cleanup).
        var candidates = comments.Select((comment, index) => new PromptTemplateModels.PromptImportanceRankingCandidateModel(
            index,
            comment.Severity.ToString().ToLowerInvariant(),
            comment.Message,
            FileByFileImportanceRankingStage.ScoreComment(comment),
            false)).ToList();

        return PromptTemplateRuntime.RenderStage("importance_ranking_user", new PromptTemplateModels.ImportanceRankingUserModel(candidates));
    }

    /// <summary>
    ///     Builds the user message for the cross-file quality-filter AI pass.
    ///     Formats <paramref name="comments" /> as a numbered markdown table.
    /// </summary>
    internal static string BuildQualityFilterUserMessage(IReadOnlyList<ReviewComment> comments)
    {
        return PromptTemplateRuntime.RenderStage(
            "quality_filter_user",
            new PromptTemplateModels.QualityFilterUserModel(
                comments.Select((comment, index) => new PromptTemplateModels.PromptQualityFilterCommentModel(
                    index + 1,
                    comment.FilePath ?? "(none)",
                    comment.LineNumber?.ToString() ?? "-",
                    comment.Severity.ToString().ToLowerInvariant(),
                    comment.Message.Replace("|", @"\|"))).ToList()));
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
        return PromptTemplateRuntime.RenderStage(
            "memory_reconsideration_system",
            new PromptTemplateModels.MemoryReconsiderationSystemModel(reviewerIdentity));
    }

    /// <summary>
    ///     User message for the memory-augmented reconsideration step.
    ///     Combines draft findings JSON with formatted historical matches.
    /// </summary>
    internal static string BuildMemoryReconsiderationUserMessage(
        string draftFindingsJson,
        IReadOnlyList<ThreadMemoryMatchDto> matches)
    {
        return PromptTemplateRuntime.RenderStage(
            "memory_reconsideration_user",
            new PromptTemplateModels.MemoryReconsiderationUserModel(
                draftFindingsJson,
                matches.Select((match, index) => new PromptTemplateModels.PromptMemoryMatchModel(
                    index + 1,
                    match.SimilarityScore.ToString("F2"),
                    match.MemoryRecordId.ToString(),
                    match.FilePath,
                    match.ResolutionSummary)).ToList()));
    }
}
