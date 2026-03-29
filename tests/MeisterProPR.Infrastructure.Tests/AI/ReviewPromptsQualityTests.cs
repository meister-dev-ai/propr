using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests asserting the structural and content requirements of the rewritten
///     <see cref="ReviewPrompts.AgenticLoopGuidance" /> (IMP-06, feature 023) and the
///     quality filter prompt methods (IMP-08, feature 023).
/// </summary>
public class ReviewPromptsQualityTests
{
    // ── T004(a): Certainty Gate is the very first rule ──────────────────────────

    [Fact]
    public void AgenticLoopGuidance_StartsWithCertaintyGate()
    {
        Assert.StartsWith("CERTAINTY GATE", ReviewPrompts.AgenticLoopGuidance.TrimStart(), StringComparison.OrdinalIgnoreCase);
    }

    // ── T004(b): INFO rule appears before tool descriptions ──────────────────────

    [Fact]
    public void AgenticLoopGuidance_ContainsInfoRuleBeforeToolsSection()
    {
        var guidance = ReviewPrompts.AgenticLoopGuidance;
        var infoRuleIdx = guidance.IndexOf("INFO rule", StringComparison.OrdinalIgnoreCase);
        var toolsIdx = guidance.IndexOf("get_changed_files", StringComparison.OrdinalIgnoreCase);

        Assert.True(infoRuleIdx >= 0, "AgenticLoopGuidance must contain 'INFO rule'");
        Assert.True(toolsIdx >= 0, "AgenticLoopGuidance must contain tool names");
        Assert.True(infoRuleIdx < toolsIdx, "INFO rule must appear before tool descriptions");
    }

    // ── T004(c): SUGGESTION rule appears before tool descriptions ────────────────

    [Fact]
    public void AgenticLoopGuidance_ContainsSuggestionRuleBeforeToolsSection()
    {
        var guidance = ReviewPrompts.AgenticLoopGuidance;
        var suggRuleIdx = guidance.IndexOf("SUGGESTION rule", StringComparison.OrdinalIgnoreCase);
        var toolsIdx = guidance.IndexOf("get_changed_files", StringComparison.OrdinalIgnoreCase);

        Assert.True(suggRuleIdx >= 0, "AgenticLoopGuidance must contain 'SUGGESTION rule'");
        Assert.True(toolsIdx >= 0, "AgenticLoopGuidance must contain tool names");
        Assert.True(suggRuleIdx < toolsIdx, "SUGGESTION rule must appear before tool descriptions");
    }

    // ── T004(d): Old 'Verification rule' heading is removed ─────────────────────

    [Fact]
    public void AgenticLoopGuidance_DoesNotContainStandaloneVerificationRuleHeading()
    {
        // The heading "Verification rule:" must not exist as a standalone rule label
        Assert.DoesNotContain("Verification rule:", ReviewPrompts.AgenticLoopGuidance, StringComparison.OrdinalIgnoreCase);
    }

    // ── T004(e): Old 'Findings validation rule' heading is removed ───────────────

    [Fact]
    public void AgenticLoopGuidance_DoesNotContainFindingsValidationRuleHeading()
    {
        Assert.DoesNotContain("Findings validation rule", ReviewPrompts.AgenticLoopGuidance, StringComparison.OrdinalIgnoreCase);
    }

    // ── T004(f): Old 'SUGGESTION batching rule' heading is removed ───────────────

    [Fact]
    public void AgenticLoopGuidance_DoesNotContainSuggestionBatchingRuleHeading()
    {
        Assert.DoesNotContain("SUGGESTION batching rule", ReviewPrompts.AgenticLoopGuidance, StringComparison.OrdinalIgnoreCase);
    }

    // ── T004(g): CRITICAL OUTPUT RULE appears at most once ──────────────────────

    [Fact]
    public void AgenticLoopGuidance_ContainsCriticalOutputRuleAtMostOnce()
    {
        var guidance = ReviewPrompts.AgenticLoopGuidance;
        var firstIdx = guidance.IndexOf("CRITICAL OUTPUT RULE", StringComparison.OrdinalIgnoreCase);
        if (firstIdx < 0)
        {
            // Zero occurrences is also acceptable (the rule may be only in SystemPrompt)
            return;
        }

        var secondIdx = guidance.IndexOf("CRITICAL OUTPUT RULE", firstIdx + 1, StringComparison.OrdinalIgnoreCase);
        Assert.True(secondIdx < 0, "CRITICAL OUTPUT RULE must appear at most once in AgenticLoopGuidance");
    }

    // ── T018: BuildQualityFilterSystemPrompt content ────────────────────────────

    [Fact]
    public void BuildQualityFilterSystemPrompt_IsNonEmpty()
    {
        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(null);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    [Fact]
    public void BuildQualityFilterSystemPrompt_ContainsDiscard()
    {
        Assert.Contains("DISCARD", ReviewPrompts.BuildQualityFilterSystemPrompt(null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQualityFilterSystemPrompt_ContainsSpeculativeLanguage()
    {
        Assert.Contains("speculative language", ReviewPrompts.BuildQualityFilterSystemPrompt(null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQualityFilterSystemPrompt_ContainsInfoSeverity()
    {
        Assert.Contains("INFO-severity", ReviewPrompts.BuildQualityFilterSystemPrompt(null), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQualityFilterSystemPrompt_ContainsDuplicate()
    {
        Assert.Contains("duplicate", ReviewPrompts.BuildQualityFilterSystemPrompt(null), StringComparison.OrdinalIgnoreCase);
    }

    // ── T019: BuildQualityFilterUserMessage formatting ──────────────────────────

    [Fact]
    public void BuildQualityFilterUserMessage_WithThreeComments_ProducesTableWithThreeDataRows()
    {
        var comments = new List<ReviewComment>
        {
            new("src/Foo.cs", 10, CommentSeverity.Error, "error message"),
            new("src/Bar.cs", 20, CommentSeverity.Warning, "warning message"),
            new("src/Baz.cs", null, CommentSeverity.Suggestion, "suggestion message"),
        };

        var result = ReviewPrompts.BuildQualityFilterUserMessage(comments);

        // Count data rows (lines starting with "| 1 |", "| 2 |", "| 3 |")
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataRows = lines.Count(l => l.TrimStart().StartsWith("|") && !l.Contains("---") && !l.Contains("File") && !l.Contains("#"));
        Assert.True(dataRows >= 3, $"Expected at least 3 data rows, got: {dataRows}. Output:\n{result}");
    }

    [Fact]
    public void BuildQualityFilterUserMessage_WithPipeInMessage_EscapesPipe()
    {
        var comments = new List<ReviewComment>
        {
            new("src/Foo.cs", 1, CommentSeverity.Warning, "use a | b instead"),
        };

        var result = ReviewPrompts.BuildQualityFilterUserMessage(comments);

        // The raw pipe in the message must be escaped as \|
        Assert.Contains(@"\|", result);
    }

    [Fact]
    public void BuildQualityFilterUserMessage_WithZeroComments_ContainsHeaderRowOnly()
    {
        var result = ReviewPrompts.BuildQualityFilterUserMessage([]);

        // Must still have a header row
        Assert.Contains("| #", result);
        Assert.Contains("File", result);

        // Must not have data rows (no "| 1 |" pattern)
        Assert.DoesNotContain("| 1 |", result);
    }
}
