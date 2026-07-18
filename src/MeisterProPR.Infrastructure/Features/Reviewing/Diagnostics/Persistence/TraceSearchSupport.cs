// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

internal static class TraceSearchSupport
{
    public static string? NormalizeEventCategory(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    public static string DeriveEventCategory(ProtocolEventKind kind, string? name)
    {
        var normalizedName = name?.Trim().ToLowerInvariant() ?? string.Empty;

        if (kind == ProtocolEventKind.MemoryOperation)
        {
            return "memory";
        }

        if (normalizedName.StartsWith("dedup_", StringComparison.Ordinal))
        {
            return "duplicate-suppression";
        }

        if (normalizedName.StartsWith("publication_", StringComparison.Ordinal))
        {
            return "publication";
        }

        if (normalizedName.Contains("comment_relevance", StringComparison.Ordinal))
        {
            return "comment-relevance";
        }

        if (normalizedName.Contains("verification", StringComparison.Ordinal))
        {
            return "verification";
        }

        if (normalizedName.Contains("review_finding_gate", StringComparison.Ordinal)
            || normalizedName.Contains("summary_reconciliation", StringComparison.Ordinal)
            || normalizedName.Contains("repeated_judgment", StringComparison.Ordinal))
        {
            return "review-finding-gate";
        }

        if (normalizedName.Contains("prorv", StringComparison.Ordinal))
        {
            return "prorv-prefilter";
        }

        if (normalizedName.Contains("pr_wide", StringComparison.Ordinal))
        {
            return "pr-wide-review";
        }

        if (normalizedName.Contains("review_strategy", StringComparison.Ordinal)
            || normalizedName.Contains("agentic_file", StringComparison.Ordinal)
            || normalizedName.Contains("review_agent_session", StringComparison.Ordinal)
            || normalizedName.Contains("prompt_stage_evidence", StringComparison.Ordinal)
            || normalizedName.Contains("review_step_skipped", StringComparison.Ordinal))
        {
            return "review-strategy";
        }

        return kind switch
        {
            ProtocolEventKind.AiCall => "ai-call",
            ProtocolEventKind.ToolCall => "tool-call",
            _ => "operational",
        };
    }
}
