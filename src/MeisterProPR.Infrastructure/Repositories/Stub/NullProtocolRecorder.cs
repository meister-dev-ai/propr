using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Repositories.Stub;

/// <summary>
///     No-op implementation of <see cref="IProtocolRecorder" /> used in non-DB (in-memory) mode.
///     All calls are silently discarded. <see cref="BeginAsync" /> returns a random <see cref="Guid" />.
/// </summary>
public sealed class NullProtocolRecorder : IProtocolRecorder
{
    /// <inheritdoc />
    public Task<Guid> BeginAsync(Guid jobId, int attemptNumber, string? label = null, Guid? fileResultId = null, CancellationToken ct = default)
    {
        return Task.FromResult(Guid.NewGuid());
    }

    /// <inheritdoc />
    public Task RecordAiCallAsync(
        Guid protocolId,
        int iteration,
        long? inputTokens,
        long? outputTokens,
        string? inputTextSample,
        string? outputTextSample,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordToolCallAsync(Guid protocolId, string toolName, string arguments, string result, int iteration, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetCompletedAsync(
        Guid protocolId,
        string outcome,
        long totalInputTokens,
        long totalOutputTokens,
        int iterationCount,
        int toolCallCount,
        int? finalConfidence,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
