namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     Captures the observable metrics produced by one agentic review loop execution.
/// </summary>
/// <param name="ToolCallCount">Total number of tool calls made during the loop.</param>
/// <param name="ToolCallsJson">
///     JSON-serialised array of <c>ReviewToolCall</c> records, or <see langword="null" /> when
///     none were recorded.
/// </param>
/// <param name="ConfidenceEvaluationsJson">
///     JSON-serialised array of confidence evaluations captured across all iterations,
///     or <see langword="null" /> when none were recorded.
/// </param>
/// <param name="FinalConfidence">
///     Final aggregated confidence score (0–100) at loop exit, or <see langword="null" /> when
///     unavailable.
/// </param>
/// <param name="TotalInputTokens">Sum of input tokens across all AI calls in the loop.</param>
/// <param name="TotalOutputTokens">Sum of output tokens across all AI calls in the loop.</param>
/// <param name="Iterations">Number of loop iterations completed.</param>
public sealed record ReviewLoopMetrics(
    int ToolCallCount,
    string? ToolCallsJson,
    string? ConfidenceEvaluationsJson,
    int? FinalConfidence,
    long TotalInputTokens,
    long TotalOutputTokens,
    int Iterations);
