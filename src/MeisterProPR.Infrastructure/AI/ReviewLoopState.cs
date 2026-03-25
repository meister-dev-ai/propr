using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>Mutable state tracked across iterations of the agentic review loop.</summary>
internal sealed class ReviewLoopState
{
    /// <summary>Gets or sets the current iteration number (one-based).</summary>
    public int Iteration { get; set; } = 1;

    /// <summary>Gets the total number of tool calls recorded so far.</summary>
    public int ToolCallCount { get; private set; }

    /// <summary>Gets the cumulative input token count across all AI calls.</summary>
    public long TotalInputTokens { get; private set; }

    /// <summary>Gets the cumulative output token count across all AI calls.</summary>
    public long TotalOutputTokens { get; private set; }

    /// <summary>Gets the ordered history of tool invocations.</summary>
    public List<ReviewToolCall> ToolCallHistory { get; } = [];

    /// <summary>Gets the ordered history of confidence snapshots.</summary>
    public List<ConfidenceSnapshot> ConfidenceSnapshots { get; } = [];

    /// <summary>Gets the accumulated chat message list for the ongoing conversation.</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>Records a single tool invocation.</summary>
    /// <param name="toolName">Name of the tool called.</param>
    /// <param name="arguments">Serialised arguments passed to the tool.</param>
    /// <param name="result">Serialised result returned by the tool.</param>
    public void RecordToolCall(string toolName, string arguments, string result)
    {
        this.ToolCallHistory.Add(new ReviewToolCall(toolName, arguments, result, DateTimeOffset.UtcNow));
        this.ToolCallCount++;
    }

    /// <summary>Accumulates token usage from one AI response.</summary>
    /// <param name="inputTokens">Input token count reported by the response, or <see langword="null" /> when unavailable.</param>
    /// <param name="outputTokens">Output token count reported by the response, or <see langword="null" /> when unavailable.</param>
    public void AccumulateTokens(long? inputTokens, long? outputTokens)
    {
        this.TotalInputTokens += inputTokens ?? 0L;
        this.TotalOutputTokens += outputTokens ?? 0L;
    }

    /// <summary>Records a snapshot of confidence scores produced at the current iteration.</summary>
    /// <param name="scores">Confidence scores reported by the AI for this iteration.</param>
    public void RecordConfidenceSnapshot(IReadOnlyList<ConfidenceScore> scores)
    {
        this.ConfidenceSnapshots.Add(new ConfidenceSnapshot(this.Iteration, scores, DateTimeOffset.UtcNow));
    }
}

/// <summary>A point-in-time snapshot of confidence scores captured at a specific loop iteration.</summary>
/// <param name="Iteration">Loop iteration number at which the snapshot was taken.</param>
/// <param name="Scores">Individual concern confidence scores.</param>
/// <param name="RecordedAt">UTC timestamp at which the snapshot was recorded.</param>
internal sealed record ConfidenceSnapshot(
    int Iteration,
    IReadOnlyList<ConfidenceScore> Scores,
    DateTimeOffset RecordedAt);
