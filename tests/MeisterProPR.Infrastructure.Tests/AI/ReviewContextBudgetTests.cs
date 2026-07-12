// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewContextBudgetTests
{
    // The heuristic estimator is 4 chars per token; sizing strings by length gives deterministic token counts.
    private static string Chars(int count) => new('a', count);

    private static ChatMessage System(int chars) => new(ChatRole.System, Chars(chars));

    private static ChatMessage User(int chars) => new(ChatRole.User, Chars(chars));

    private static ChatMessage Tool(int chars, string callId = "call-1") =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, Chars(chars))]);

    [Fact]
    public void ResolveMaxContextTokens_uses_default_when_unset_or_invalid()
    {
        Assert.Equal(ReviewContextBudget.DefaultMaxContextTokens, ReviewContextBudget.ResolveMaxContextTokens(null));
        Assert.Equal(ReviewContextBudget.DefaultMaxContextTokens, ReviewContextBudget.ResolveMaxContextTokens(0));
        Assert.Equal(ReviewContextBudget.DefaultMaxContextTokens, ReviewContextBudget.ResolveMaxContextTokens(-5));
        Assert.Equal(200_000, ReviewContextBudget.ResolveMaxContextTokens(200_000));
    }

    [Fact]
    public void ComputeInputBudget_subtracts_reserved_output_and_margin_and_floors_at_one()
    {
        var budget = ReviewContextBudget.ComputeInputBudget(128_000, 8_000);
        Assert.Equal(128_000 - 8_000 - ReviewContextBudget.SafetyMarginTokens, budget);

        // A tiny window that the reserve alone exceeds still yields a positive (floored) budget.
        Assert.Equal(1, ReviewContextBudget.ComputeInputBudget(1_000, 100_000));
    }

    [Fact]
    public void EstimateTokens_falls_back_to_heuristic_for_unknown_tokenizer()
    {
        var text = Chars(400); // 100 tokens under the heuristic
        Assert.Equal(100, ReviewContextBudget.EstimateTokens(null, text));
        Assert.Equal(100, ReviewContextBudget.EstimateTokens("not-a-real-tokenizer", text));
    }

    [Theory]
    [InlineData("cl100k_base")]
    [InlineData("o200k_base")]
    [InlineData("o200k_harmony")]
    [InlineData("claude")]
    public void EstimateTokens_never_throws_for_registry_advertised_encodings(string tokenizer)
    {
        // Some advertised encodings may not resolve in every tokenizer build. Budgeting must never throw for
        // any of them — it either counts exactly or falls back to the heuristic, always returning a positive count.
        var estimate = ReviewContextBudget.EstimateTokens(tokenizer, Chars(400));
        Assert.True(estimate > 0, $"tokenizer '{tokenizer}' produced a non-positive estimate");
    }

    [Fact]
    public void EstimateTokens_uses_exact_tokenizer_when_supported()
    {
        var estimate = ReviewContextBudget.EstimateTokens("cl100k_base", "the quick brown fox jumps over the lazy dog");
        Assert.True(estimate > 0);
    }

    [Fact]
    public void EstimateTokens_is_zero_for_empty_text()
    {
        Assert.Equal(0, ReviewContextBudget.EstimateTokens("cl100k_base", null));
        Assert.Equal(0, ReviewContextBudget.EstimateTokens(null, string.Empty));
    }

    [Fact]
    public void ClassifyInitialPayload_within_budget_when_full_payload_fits()
    {
        var messages = new List<ChatMessage> { System(40), System(40), User(40) }; // 30 tokens total

        var result = ReviewContextBudget.ClassifyInitialPayload(messages, tokenizerName: null, inputBudget: 100, maxContextTokens: 128_000);

        Assert.Equal(ContextBudgetClassification.WithinBudget, result.Classification);
        Assert.Equal(30, result.EstimatedInputTokens);
    }

    [Fact]
    public void ClassifyInitialPayload_degrades_when_only_file_content_windows_overflow()
    {
        // S1 (10) + S2 prefetch window (100) + diff (10) = 120 full; minimal (S1 + diff) = 20.
        var messages = new List<ChatMessage> { System(40), System(400), User(40) };

        var result = ReviewContextBudget.ClassifyInitialPayload(messages, tokenizerName: null, inputBudget: 50, maxContextTokens: 128_000);

        Assert.Equal(ContextBudgetClassification.DegradedDiffOnly, result.Classification);
    }

    [Fact]
    public void ClassifyInitialPayload_skips_when_minimal_payload_overflows()
    {
        // The diff alone (1000 tokens) exceeds the budget, so even diff-only cannot fit.
        var messages = new List<ChatMessage> { System(40), User(4_000) };

        var result = ReviewContextBudget.ClassifyInitialPayload(messages, tokenizerName: null, inputBudget: 100, maxContextTokens: 128_000);

        Assert.Equal(ContextBudgetClassification.Skipped, result.Classification);
    }

    [Fact]
    public void ToDiffOnly_keeps_first_system_and_all_user_messages_dropping_extra_system_messages()
    {
        var messages = new List<ChatMessage> { System(40), System(400), User(40) };

        var diffOnly = ReviewContextBudget.ToDiffOnly(messages);

        Assert.Equal(2, diffOnly.Count);
        Assert.Equal(ChatRole.System, diffOnly[0].Role);
        Assert.Equal(ChatRole.User, diffOnly[1].Role);
    }

    [Fact]
    public void TrimToolHistory_leaves_messages_untouched_when_within_budget()
    {
        var messages = new List<ChatMessage> { System(40), User(40), Tool(40) };

        var result = ReviewContextBudget.TrimToolHistoryToBudget(messages, tokenizerName: null, inputBudget: 100);

        Assert.False(result.Trimmed);
        Assert.Equal(0, result.CompactedMessageCount);
        Assert.Same(messages, result.Messages);
    }

    [Fact]
    public void TrimToolHistory_compacts_tool_results_oldest_first_until_within_budget()
    {
        // sys 10 + user 10 + two 500-token tool results = 1020 tokens; budget 100.
        var messages = new List<ChatMessage> { System(40), User(40), Tool(2_000, "a"), Tool(2_000, "b") };

        var result = ReviewContextBudget.TrimToolHistoryToBudget(messages, tokenizerName: null, inputBudget: 100);

        Assert.True(result.Trimmed);
        Assert.True(result.CompactedMessageCount >= 1);
        Assert.True(
            result.EstimatedTokensAfter <= 100,
            $"expected trimmed payload within budget but was {result.EstimatedTokensAfter}");
        Assert.True(result.EstimatedTokensAfter < result.EstimatedTokensBefore);

        // Compacting preserves the tool role and the call identity so request/result pairing survives.
        var toolMessages = result.Messages.Where(message => message.Role == ChatRole.Tool).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Contains(
            toolMessages,
            message => message.Contents.OfType<FunctionResultContent>().Any(content => content.CallId == "a"));
    }

    [Fact]
    public void TrimToolHistory_brings_a_synthetic_oversize_file_read_within_the_window()
    {
        // A single tool read far larger than a 128k window — the overflow case the fix exists to prevent.
        var maxContext = ReviewContextBudget.DefaultMaxContextTokens;
        var budget = ReviewContextBudget.ComputeInputBudget(maxContext, reservedOutputTokens: 8_000);
        var messages = new List<ChatMessage>
        {
            System(2_000),
            User(2_000),
            Tool(4_000_000), // ~1,000,000 tokens under the heuristic — an order of magnitude over the window
        };

        var result = ReviewContextBudget.TrimToolHistoryToBudget(messages, tokenizerName: null, budget);

        Assert.True(result.Trimmed);
        Assert.True(
            result.EstimatedTokensAfter <= budget,
            $"expected trimmed payload within budget {budget} but was {result.EstimatedTokensAfter}");
    }

    [Fact]
    public void TrimToolHistory_compacts_old_assistant_turns_when_no_tool_history_remains()
    {
        // sys 10 + user 10 + a large OLD assistant reasoning turn (500 tok) + a recent assistant turn (10).
        // With no tool messages to compact, the fallback must compact the old assistant turn while preserving
        // the most recent one, so the payload still fits.
        var recentReasoning = Chars(40);
        var messages = new List<ChatMessage>
        {
            System(40),
            User(40),
            new(ChatRole.Assistant, Chars(2_000)),
            new(ChatRole.Assistant, recentReasoning),
        };

        var result = ReviewContextBudget.TrimToolHistoryToBudget(messages, tokenizerName: null, inputBudget: 100);

        Assert.True(result.Trimmed);
        Assert.True(
            result.EstimatedTokensAfter <= 100,
            $"expected trimmed payload within budget but was {result.EstimatedTokensAfter}");
        Assert.Equal(recentReasoning, result.Messages[^1].Text);
    }
}
