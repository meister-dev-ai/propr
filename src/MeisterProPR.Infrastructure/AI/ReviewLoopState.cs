// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
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

    /// <summary>Gets the logical review session tracked for this loop.</summary>
    public AgentReviewSession Session { get; private set; } = new(
        Guid.NewGuid().ToString("n"),
        AgentReviewSessionMode.StatelessReplay,
        AgentReviewSessionStatus.Active,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        [],
        []);

    /// <summary>Gets the ordered turn-submission history.</summary>
    public List<TurnContextSubmission> TurnHistory { get; } = [];

    /// <summary>Gets the durable system messages that should be preserved when replay is required.</summary>
    public List<ChatMessage> PersistentMessages { get; } = [];

    /// <summary>Gets the compacted summary of bulky tool context retained for later turns.</summary>
    public string? CompactedPayloadSummary { get; private set; }

    /// <summary>Gets the current replay summary used for diagnostics.</summary>
    public string? ReplayedPayloadSummary { get; private set; }

    /// <summary>Gets the in-memory provider continuation token used for provider-managed turns.</summary>
    public ResponseContinuationToken? ProviderContinuationToken { get; private set; }

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

    /// <summary>Initializes the logical session for the loop.</summary>
    public void InitializeSession(AgentReviewSessionMode mode)
    {
        var now = DateTimeOffset.UtcNow;
        this.Session = new AgentReviewSession(
            this.Session.LocalSessionId,
            mode,
            AgentReviewSessionStatus.Active,
            this.Session.StartedAt,
            now,
            this.Session.ContinuationHandle,
            this.Session.WorkingMemory,
            this.Session.Fallbacks);
    }

    /// <summary>Sets the durable replay baseline used for later stateless fallback or compaction.</summary>
    public void SetPersistentMessages(IEnumerable<ChatMessage> messages)
    {
        this.PersistentMessages.Clear();
        this.PersistentMessages.AddRange(messages);
        this.ReplayedPayloadSummary = DescribeReplay(this.PersistentMessages);
    }

    /// <summary>Updates the current provider-managed continuation handle.</summary>
    public void UpdateContinuationHandle(SessionContinuationHandle? handle, ResponseContinuationToken? continuationToken)
    {
        var now = DateTimeOffset.UtcNow;
        this.ProviderContinuationToken = continuationToken;
        this.Session = this.Session with
        {
            ContinuationHandle = handle,
            LastUpdatedAt = now,
        };
    }

    /// <summary>Compacts the mutable replay transcript into concise working memory.</summary>
    public void CompactReplayHistory()
    {
        if (this.Messages.Count <= this.PersistentMessages.Count)
        {
            return;
        }

        var messagesToCompact = this.Messages.Skip(this.PersistentMessages.Count).ToList();
        if (messagesToCompact.Count == 0)
        {
            return;
        }

        var summaries = new List<string>();
        var replacedPayloadCount = 0;

        foreach (var message in messagesToCompact)
        {
            if (message.Role == ChatRole.Tool)
            {
                var toolResults = message.Contents.OfType<FunctionResultContent>().ToList();
                if (toolResults.Count == 0)
                {
                    continue;
                }

                replacedPayloadCount += toolResults.Count;
                summaries.Add($"tool results compacted: {string.Join(", ", toolResults.Select(result => result.CallId))}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                replacedPayloadCount++;
                summaries.Add($"{message.Role.Value}: {SummarizeText(message.Text)}");
            }
        }

        if (replacedPayloadCount == 0)
        {
            return;
        }

        var summaryText = string.Join(" | ", summaries.Distinct(StringComparer.Ordinal));
        this.CompactedPayloadSummary = summaryText;
        var workingMemory = this.Session.WorkingMemory.ToList();
        workingMemory.Add(
            new WorkingMemorySummary(
                Guid.NewGuid().ToString("n"),
                "tool_batch",
                ["review_loop"],
                summaryText,
                replacedPayloadCount,
                DateTimeOffset.UtcNow));

        var replayTail = GetReplayTail(messagesToCompact);

        this.Messages.Clear();
        this.Messages.AddRange(this.PersistentMessages);
        this.Messages.AddRange(replayTail);

        this.Session = this.Session with
        {
            Status = AgentReviewSessionStatus.Compacted,
            WorkingMemory = workingMemory.AsReadOnly(),
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        this.ReplayedPayloadSummary = DescribeReplay(this.Messages);
    }

    /// <summary>Records one turn's context-submission metadata.</summary>
    public void RecordTurn(
        ReviewTurnContextStrategy strategy,
        string? newInputSummary,
        long? inputTokens,
        long? outputTokens)
    {
        var turn = new TurnContextSubmission(
            this.Iteration,
            strategy,
            this.Session.Mode,
            newInputSummary,
            this.ReplayedPayloadSummary,
            this.CompactedPayloadSummary,
            inputTokens,
            outputTokens,
            this.Session.ContinuationHandle?.HandleValue,
            this.Session.ContinuationHandle?.ProviderSessionId,
            this.Session.ContinuationHandle?.ProviderResponseId,
            DateTimeOffset.UtcNow);
        this.TurnHistory.Add(turn);
    }

    /// <summary>Downgrades the session to a safer mode and records the fallback.</summary>
    public void RecordFallback(AgentReviewSessionMode toMode, string reason, string preservedState)
    {
        if (this.Session.Mode == toMode)
        {
            return;
        }

        var fallbacks = this.Session.Fallbacks.ToList();
        fallbacks.Add(
            new SessionFallbackRecord(
                Guid.NewGuid().ToString("n"),
                this.Session.Mode,
                toMode,
                reason,
                this.Iteration,
                preservedState,
                DateTimeOffset.UtcNow));

        this.Session = this.Session with
        {
            Mode = toMode,
            Status = AgentReviewSessionStatus.Downgraded,
            Fallbacks = fallbacks.AsReadOnly(),
            ContinuationHandle = null,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        this.ProviderContinuationToken = null;
    }

    /// <summary>Marks the session as successfully completed.</summary>
    public void MarkCompleted()
    {
        this.Session = this.Session with
        {
            Status = AgentReviewSessionStatus.Completed,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Marks the session as failed.</summary>
    public void MarkFailed()
    {
        this.Session = this.Session with
        {
            Status = AgentReviewSessionStatus.Failed,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string DescribeReplay(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return "none";
        }

        var parts = messages.Select(message => message.Role == ChatRole.Tool
                ? "tool_result"
                : $"{message.Role.Value}:{SummarizeText(message.Text)}")
            .ToArray();
        return string.Join(" | ", parts);
    }

    private static string SummarizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty";
        }

        const int maxLength = 80;
        var normalized = text.Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    private static List<ChatMessage> GetReplayTail(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var latestMessage = messages[^1];
        if (latestMessage.Role == ChatRole.System)
        {
            return [];
        }

        var replayTail = new List<ChatMessage>();
        if (latestMessage.Role == ChatRole.Tool &&
            messages.Count >= 2 &&
            messages[^2].Role == ChatRole.Assistant &&
            messages[^2].Contents.OfType<FunctionCallContent>().Any())
        {
            replayTail.Add(messages[^2]);
        }

        replayTail.Add(latestMessage);
        return replayTail;
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
