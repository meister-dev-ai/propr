namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Write-only service for recording agentic review protocol events incrementally during job execution.
///     All methods except <see cref="BeginAsync" /> must never throw — failures are caught and logged internally.
///     Protocol data is read back through
///     <see cref="IJobRepository.GetByIdWithProtocolAsync" /> (via the
///     <see cref="MeisterProPR.Domain.Entities.ReviewJob" /> aggregate root).
/// </summary>
public interface IProtocolRecorder
{
    /// <summary>
    ///     Creates a new protocol record for the given job attempt and returns its identifier.
    ///     Unlike the other methods, this call is allowed to throw on failure — the caller decides
    ///     whether to continue without a protocol.
    /// </summary>
    /// <param name="jobId">The review job this protocol belongs to.</param>
    /// <param name="attemptNumber">Attempt ordinal (1-based).</param>
    /// <param name="label">Human-readable label for this protocol pass (e.g. file path or "synthesis").</param>
    /// <param name="fileResultId">The file result this protocol pass belongs to, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new protocol record's <see cref="Guid" />.</returns>
    Task<Guid> BeginAsync(Guid jobId, int attemptNumber, string? label = null, Guid? fileResultId = null, CancellationToken ct = default);

    /// <summary>
    ///     Records a single AI call event. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="iteration">Loop iteration number (one-based).</param>
    /// <param name="inputTokens">Input token count reported by the AI provider, or <see langword="null" />.</param>
    /// <param name="outputTokens">Output token count reported by the AI provider, or <see langword="null" />.</param>
    /// <param name="inputTextSample">Truncated input text (≤ 50,000 characters), or <see langword="null" />.</param>
    /// <param name="outputTextSample">Truncated AI response text (≤ 50,000 characters), or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAiCallAsync(
        Guid protocolId,
        int iteration,
        long? inputTokens,
        long? outputTokens,
        string? inputTextSample,
        string? outputTextSample,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a single tool call event. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="toolName">Name of the tool that was invoked.</param>
    /// <param name="arguments">Serialised arguments passed to the tool (truncated to 4,000 characters).</param>
    /// <param name="result">Serialised result returned by the tool (truncated to 1,000 characters).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordToolCallAsync(
        Guid protocolId,
        string toolName,
        string arguments,
        string result,
        CancellationToken ct = default);

    /// <summary>
    ///     Marks the protocol as complete and persists all aggregate summary fields. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol to close.</param>
    /// <param name="outcome"><c>"completed"</c> or <c>"failed"</c>.</param>
    /// <param name="totalInputTokens">Sum of input tokens across all AI calls.</param>
    /// <param name="totalOutputTokens">Sum of output tokens across all AI calls.</param>
    /// <param name="iterationCount">Number of loop iterations completed.</param>
    /// <param name="toolCallCount">Total number of tool invocations.</param>
    /// <param name="finalConfidence">Final aggregated confidence score, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetCompletedAsync(
        Guid protocolId,
        string outcome,
        long totalInputTokens,
        long totalOutputTokens,
        int iterationCount,
        int toolCallCount,
        int? finalConfidence,
        CancellationToken ct = default);
}
