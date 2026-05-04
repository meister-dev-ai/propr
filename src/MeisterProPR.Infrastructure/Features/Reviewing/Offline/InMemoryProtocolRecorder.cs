// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory <see cref="IProtocolRecorder" /> used by offline review execution.
/// </summary>
public sealed class InMemoryProtocolRecorder(InMemoryReviewJobRepository jobs) : IProtocolRecorder
{
    public Task<Guid> BeginAsync(
        Guid jobId,
        int attemptNumber,
        string? label = null,
        Guid? fileResultId = null,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var job = jobs.GetById(jobId) ?? throw new InvalidOperationException($"Review job {jobId} was not found.");
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            AttemptNumber = attemptNumber,
            Label = label,
            FileResultId = fileResultId,
            StartedAt = DateTimeOffset.UtcNow,
            AiConnectionCategory = connectionCategory,
            ModelId = modelId,
        };

        job.Protocols.Add(protocol);
        return Task.FromResult(protocol.Id);
    }

    public Task RecordAiCallAsync(
        Guid protocolId,
        int iteration,
        long? inputTokens,
        long? outputTokens,
        string? inputTextSample,
        string? systemPrompt,
        string? outputTextSample,
        CancellationToken ct = default,
        string? name = null,
        string? error = null)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(new ProtocolEvent
        {
            Id = Guid.NewGuid(),
            ProtocolId = protocolId,
            Kind = ProtocolEventKind.AiCall,
            Name = name ?? $"ai_call_iter_{iteration}",
            OccurredAt = DateTimeOffset.UtcNow,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            InputTextSample = Sanitize(inputTextSample),
            SystemPrompt = Sanitize(systemPrompt),
            OutputSummary = Sanitize(outputTextSample),
            Error = Sanitize(error),
        });

        return Task.CompletedTask;
    }

    public Task RecordToolCallAsync(
        Guid protocolId,
        string toolName,
        string arguments,
        string result,
        int iteration,
        CancellationToken ct = default)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(new ProtocolEvent
        {
            Id = Guid.NewGuid(),
            ProtocolId = protocolId,
            Kind = ProtocolEventKind.ToolCall,
            Name = toolName,
            OccurredAt = DateTimeOffset.UtcNow,
            InputTextSample = Sanitize($"args={arguments}"),
            OutputSummary = Sanitize(result),
        });

        return Task.CompletedTask;
    }

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
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.CompletedAt = DateTimeOffset.UtcNow;
        protocol.Outcome = outcome;
        protocol.TotalInputTokens = totalInputTokens;
        protocol.TotalOutputTokens = totalOutputTokens;
        protocol.IterationCount = iterationCount;
        protocol.ToolCallCount = toolCallCount;
        protocol.FinalConfidence = finalConfidence;

        var job = jobs.GetById(protocol.JobId);
        if (job is not null)
        {
            job.AccumulateTierTokens(
                protocol.AiConnectionCategory ?? AiConnectionModelCategory.Default,
                protocol.ModelId ?? "(default)",
                totalInputTokens,
                totalOutputTokens);
        }

        return Task.CompletedTask;
    }

    public Task AddTokensAsync(
        Guid protocolId,
        long inputTokens,
        long outputTokens,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.TotalInputTokens = (protocol.TotalInputTokens ?? 0) + inputTokens;
        protocol.TotalOutputTokens = (protocol.TotalOutputTokens ?? 0) + outputTokens;

        var job = jobs.GetById(protocol.JobId);
        if (job is not null)
        {
            job.AccumulateTierTokens(
                connectionCategory ?? AiConnectionModelCategory.Default,
                modelId ?? protocol.ModelId ?? "(default)",
                inputTokens,
                outputTokens);
        }

        return Task.CompletedTask;
    }

    public Task RecordMemoryEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordOperationalEventAsync(protocolId, eventName, details, null, error);
    }

    public Task RecordDedupEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordOperationalEventAsync(protocolId, eventName, details, null, error);
    }

    public Task RecordCommentRelevanceEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordOperationalEventAsync(protocolId, eventName, details, output, error);
    }

    public Task RecordReviewFindingGateEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordOperationalEventAsync(protocolId, eventName, details, output, error);
    }

    public Task RecordVerificationEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordOperationalEventAsync(protocolId, eventName, details, output, error);
    }

    private Task RecordOperationalEventAsync(Guid protocolId, string eventName, string? details, string? output, string? error)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(new ProtocolEvent
        {
            Id = Guid.NewGuid(),
            ProtocolId = protocolId,
            Kind = ProtocolEventKind.Operational,
            Name = eventName,
            OccurredAt = DateTimeOffset.UtcNow,
            InputTextSample = Sanitize(details),
            OutputSummary = Sanitize(output),
            Error = Sanitize(error),
        });

        return Task.CompletedTask;
    }

    private ReviewJobProtocol? FindProtocol(Guid protocolId)
    {
        return jobs.GetAllJobsAsync(int.MaxValue, 0, null).Result.items
            .SelectMany(job => job.Protocols)
            .FirstOrDefault(protocol => protocol.Id == protocolId);
    }

    private static string? Sanitize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text.Replace("\0", string.Empty, StringComparison.Ordinal);
    }
}
