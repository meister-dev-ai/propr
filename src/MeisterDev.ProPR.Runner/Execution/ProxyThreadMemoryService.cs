// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Thread memory for a review running on a host that has no memory store: retrieval and
///     reconsideration go to the control plane, which answers with the reconsidered draft.
///     <para>
///         Fail-soft in every direction, because that is this interface's contract and because a review
///         without memory is a legitimate review. A refusal, an older control plane without the operation,
///         or an unreachable network all leave the draft as it was — recorded on the trace, so a remote
///         review that ran without memory says so instead of reading like one that found nothing.
///     </para>
///     <para>
///         Only reconsideration is served. The other operations on this interface belong to the crawl,
///         publication, and admin paths, which never run on an executor; each throws rather than
///         pretending it wrote to a store this host does not have.
///     </para>
/// </summary>
public sealed partial class ProxyThreadMemoryService(
    HttpClient http,
    Guid jobId,
    int leaseGeneration,
    IProtocolRecorder recorder,
    ILogger<ProxyThreadMemoryService> logger) : IThreadMemoryService
{
    /// <summary>
    ///     The control plane writes enums as camel-case strings; the plain web defaults this host reads
    ///     other envelopes with would reject a comment's severity.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <inheritdoc />
    public async Task<ReviewResult> RetrieveAndReconsiderAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct = default,
        float? temperature = null,
        ReviewSystemContext? reviewContext = null)
    {
        ArgumentNullException.ThrowIfNull(draftResult);

        try
        {
            using var response = await http.PostAsJsonAsync(
                "memory/reconsider",
                new
                {
                    jobId,
                    leaseGeneration,
                    contractVersion = RunnerContractVersion.Current,
                    filePath,
                    changeExcerpt,
                    draftSummary = draftResult.Summary,
                    draftComments = draftResult.Comments,
                    temperature,
                },
                Json,
                ct);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // The lease is no longer this executor's. Memory degrades rather than stopping the file:
                // the next relay or ingest call meets the same refusal and ends the review properly.
                await this.RecordAsync(protocolId, "memory_operation_failed", filePath, "the control plane refused the lease", ct);
                return draftResult;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Includes an older control plane that does not serve the operation yet. The review goes
                // on without memory, and the trace says so.
                await this.RecordAsync(protocolId, "memory_retrieval_degraded", filePath, $"the control plane answered {(int)response.StatusCode}", ct);
                return draftResult;
            }

            var envelope = await response.Content.ReadFromJsonAsync<MemoryEnvelope>(Json, ct);
            if (envelope?.Value is null || envelope.Unavailable)
            {
                await this.RecordAsync(protocolId, "memory_retrieval_degraded", filePath, "memory is not offered on this installation", ct);
                return draftResult;
            }

            if (protocolId.HasValue)
            {
                await recorder.RecordMemoryEventAsync(
                    protocolId.Value,
                    "memory_reconsideration_completed",
                    JsonSerializer.Serialize(
                        new
                        {
                            filePath,
                            commentsBefore = draftResult.Comments.Count,
                            commentsAfter = envelope.Value.Comments.Count,
                        },
                        Json),
                    null,
                    ct);
            }

            // Summary and comments are what reconsideration produces; everything else on the draft is
            // job-level state the memory stage never touches on either side.
            return draftResult with { Summary = envelope.Value.Summary, Comments = envelope.Value.Comments };
        }
#pragma warning disable CA1031 // The interface's contract: memory failures degrade the stage, never the review.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The interface promises this never throws into a review — the in-process implementation
            // swallows even cancellation here and returns the draft, so this one does too.
            LogReconsiderationFailed(logger, filePath, ex);
            await this.RecordAsync(protocolId, "memory_operation_failed", filePath, ex.Message, CancellationToken.None);
            return draftResult;
        }
    }

    /// <inheritdoc />
    public Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        throw new NotSupportedException("Thread lifecycle events are recorded by the crawl, in the control plane.");
    }

    /// <inheritdoc />
    public Task HandleThreadReopenedAsync(ThreadReopenedDomainEvent evt, CancellationToken ct = default)
    {
        throw new NotSupportedException("Thread lifecycle events are recorded by the crawl, in the control plane.");
    }

    /// <inheritdoc />
    public Task RecordNoOpAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string threadId,
        string? previousStatus,
        string currentStatus,
        string reason,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Thread lifecycle events are recorded by the crawl, in the control plane.");
    }

    /// <inheritdoc />
    public Task<ThreadMemoryRecord> DismissFindingAsync(
        Guid clientId,
        string? filePath,
        string findingMessage,
        string? label,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Findings are dismissed through the control plane's API, which owns the store.");
    }

    /// <inheritdoc />
    public Task<HistoricalDuplicateSuppressionMatchDto> FindDuplicateSuppressionMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string findingMessage,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Duplicate suppression runs at publication, in the control plane.");
    }

    private async Task RecordAsync(Guid? protocolId, string eventName, string filePath, string reason, CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await recorder.RecordMemoryEventAsync(
            protocolId.Value,
            eventName,
            JsonSerializer.Serialize(new { filePath, reason }, Json),
            eventName == "memory_operation_failed" ? reason : null,
            ct);
    }

    [LoggerMessage(
        EventId = 6410,
        Level = LogLevel.Warning,
        Message = "Memory reconsideration for {FilePath} degraded to the draft result")]
    private static partial void LogReconsiderationFailed(ILogger logger, string filePath, Exception exception);

    /// <summary>The envelope the execution controller wraps the reconsidered draft in.</summary>
    private sealed record MemoryEnvelope(bool Unavailable, MemoryValue? Value);

    /// <summary>The reconsidered draft: a summary and the comments that survived.</summary>
    private sealed record MemoryValue(string Summary, IReadOnlyList<ReviewComment> Comments);
}
