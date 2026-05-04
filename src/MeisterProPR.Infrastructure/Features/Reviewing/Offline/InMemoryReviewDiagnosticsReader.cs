// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory diagnostics reader for offline review execution.
/// </summary>
public sealed class InMemoryReviewDiagnosticsReader(InMemoryReviewJobRepository jobs) : IReviewDiagnosticsReader
{
    public Task<GetReviewJobProtocolResult?> GetJobProtocolAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = jobs.GetById(jobId);
        if (job is null)
        {
            return Task.FromResult<GetReviewJobProtocolResult?>(null);
        }

        var protocols = job.Protocols
            .Select(protocol => new ReviewJobProtocolDto(
                protocol.Id,
                protocol.JobId,
                protocol.AttemptNumber,
                protocol.Label,
                protocol.FileResultId,
                protocol.StartedAt,
                protocol.CompletedAt,
                protocol.Outcome,
                protocol.TotalInputTokens,
                protocol.TotalOutputTokens,
                protocol.IterationCount,
                protocol.ToolCallCount,
                protocol.FinalConfidence,
                protocol.AiConnectionCategory,
                protocol.ModelId,
                protocol.Events
                    .Select(e => new ProtocolEventDto(
                        e.Id,
                        e.Kind,
                        e.Name,
                        e.OccurredAt,
                        e.InputTokens,
                        e.OutputTokens,
                        e.InputTextSample,
                        e.SystemPrompt,
                        e.OutputSummary,
                        e.Error))
                    .ToList()
                    .AsReadOnly()))
            .ToList()
            .AsReadOnly();

        return Task.FromResult<GetReviewJobProtocolResult?>(new GetReviewJobProtocolResult(job.ClientId, protocols));
    }
}
