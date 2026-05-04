// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

/// <summary>
///     EF-backed Reviewing diagnostics reader.
/// </summary>
public sealed class EfReviewDiagnosticsReader(IJobRepository jobRepository) : IReviewDiagnosticsReader
{
    public async Task<GetReviewJobProtocolResult?> GetJobProtocolAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdWithProtocolsAsync(jobId, ct);

        if (job is null)
        {
            return null;
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

        return new GetReviewJobProtocolResult(job.ClientId, protocols);
    }
}
