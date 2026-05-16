// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

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
                ResolveFinalSummary(job, protocol),
                ResolveFinalComments(job, protocol),
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
                    .AsReadOnly())
            {
                Provider = job.Provider,
                ProviderScopePath = job.OrganizationUrl,
                ProviderProjectKey = ResolveProviderProjectKey(job),
                RepositoryId = job.RepositoryId,
                PullRequestId = job.PullRequestId,
                ResolvedReviewStrategy = job.ReviewStrategy,
                StrategySelectionSource = job.ReviewStrategySelectionSource,
                FileOutcome = ResolveFileOutcome(job, protocol),
                FollowUp = ResolveFollowUp(protocol),
                RepeatedJudgment = ResolveRepeatedJudgment(protocol),
            })
            .ToList()
            .AsReadOnly();

        return Task.FromResult<GetReviewJobProtocolResult?>(new GetReviewJobProtocolResult(job.ClientId, protocols));
    }

    private static string? ResolveFinalSummary(ReviewJob job, ReviewJobProtocol protocol)
    {
        if (protocol.FileResultId.HasValue)
        {
            return job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value)?.PerFileSummary;
        }

        if (string.Equals(protocol.Label, "synthesis", StringComparison.OrdinalIgnoreCase))
        {
            return job.Result?.Summary;
        }

        return null;
    }

    private static IReadOnlyList<ProtocolReviewCommentDto>? ResolveFinalComments(
        ReviewJob job,
        ReviewJobProtocol protocol)
    {
        if (protocol.FileResultId.HasValue)
        {
            return job.FileReviewResults
                .FirstOrDefault(result => result.Id == protocol.FileResultId.Value)?
                .Comments?
                .Select(ToProtocolReviewCommentDto)
                .ToList()
                .AsReadOnly();
        }

        if (string.Equals(protocol.Label, "synthesis", StringComparison.OrdinalIgnoreCase))
        {
            return job.Result?.Comments
                .Select(ToProtocolReviewCommentDto)
                .ToList()
                .AsReadOnly();
        }

        return null;
    }

    private static ProtocolReviewCommentDto ToProtocolReviewCommentDto(ReviewComment comment)
    {
        return new ProtocolReviewCommentDto(comment.FilePath, comment.LineNumber, comment.Severity, comment.Message);
    }

    private static string ResolveProviderProjectKey(ReviewJob job)
    {
        var repository = job.RepositoryReference;
        return string.IsNullOrWhiteSpace(repository.OwnerOrNamespace)
            ? repository.ProjectPath
            : repository.OwnerOrNamespace;
    }

    private static ProtocolFileOutcomeDto? ResolveFileOutcome(ReviewJob job, ReviewJobProtocol protocol)
    {
        if (!protocol.FileResultId.HasValue)
        {
            return null;
        }

        var fileResult = job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value);
        if (fileResult is null)
        {
            return null;
        }

        return new ProtocolFileOutcomeDto(
            fileResult.FilePath,
            fileResult.IsComplete,
            fileResult.IsFailed,
            fileResult.IsExcluded,
            fileResult.IsCarriedForward,
            fileResult.ExclusionReason,
            fileResult.ErrorMessage,
            protocol.Events.Any(e => string.Equals(e.Name, ReviewProtocolEventNames.AgenticFileDegraded, StringComparison.Ordinal)));
    }

    private static ProtocolFollowUpDto? ResolveFollowUp(ReviewJobProtocol protocol)
    {
        if (!protocol.FileResultId.HasValue)
        {
            return null;
        }

        var used = false;
        var completedSuccessfully = false;
        var dependencyRecorded = false;
        string? triggerFamily = null;

        foreach (var evt in protocol.Events)
        {
            if (string.Equals(evt.Name, ReviewProtocolEventNames.AgenticFilePlanCreated, StringComparison.Ordinal)
                && TryGetTriggerFamilyFromPlan(evt.OutputSummary, out var planTriggerFamily))
            {
                used = true;
                triggerFamily ??= planTriggerFamily;
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.AgenticFileInvestigationResult, StringComparison.Ordinal)
                || string.Equals(evt.Name, ReviewProtocolEventNames.AgenticFileDegraded, StringComparison.Ordinal)
                || string.Equals(evt.Name, ReviewProtocolEventNames.AgenticFileFollowUpDiagnosticsOnly, StringComparison.Ordinal))
            {
                used = true;

                if (string.Equals(evt.Name, ReviewProtocolEventNames.AgenticFileInvestigationResult, StringComparison.Ordinal)
                    && TryGetString(evt.OutputSummary, "status", out var status)
                    && string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                    && (!TryGetBoolean(evt.OutputSummary, "degraded", out var degraded) || !degraded)
                    && (!TryGetBoolean(evt.OutputSummary, "diagnosticsOnly", out var diagnosticsOnly) || !diagnosticsOnly))
                {
                    completedSuccessfully = true;
                }
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.AgenticFileFollowUpDependencyRecorded, StringComparison.Ordinal))
            {
                used = true;
                dependencyRecorded = true;
                if (TryGetString(evt.InputTextSample, "triggerFamily", out var dependencyTriggerFamily))
                {
                    triggerFamily ??= dependencyTriggerFamily;
                }
            }
        }

        return used || dependencyRecorded
            ? new ProtocolFollowUpDto(used, triggerFamily, completedSuccessfully, dependencyRecorded)
            : null;
    }

    private static ProtocolRepeatedJudgmentDto? ResolveRepeatedJudgment(ReviewJobProtocol protocol)
    {
        ProtocolRepeatedJudgmentDto? latestDecision = null;

        foreach (var evt in protocol.Events)
        {
            if (!string.Equals(evt.Name, ReviewProtocolEventNames.RepeatedJudgmentDecision, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGetString(evt.InputTextSample, "findingId", out var findingId)
                || string.IsNullOrWhiteSpace(findingId)
                || !TryGetString(evt.OutputSummary, "agreementState", out var agreementState)
                || string.IsNullOrWhiteSpace(agreementState)
                || !TryGetString(evt.OutputSummary, "recommendedDisposition", out var recommendedDisposition)
                || string.IsNullOrWhiteSpace(recommendedDisposition))
            {
                continue;
            }

            TryGetString(evt.InputTextSample, "evidenceSetId", out var evidenceSetId);
            _ = TryGetBoolean(evt.OutputSummary, "usedSameEvidenceSet", out var usedSameEvidenceSet);
            latestDecision = new ProtocolRepeatedJudgmentDto(
                findingId,
                evidenceSetId,
                agreementState,
                recommendedDisposition,
                usedSameEvidenceSet,
                GetStringArray(evt.OutputSummary, "reasonCodes"));
        }

        return latestDecision;
    }

    private static bool TryGetTriggerFamilyFromPlan(string? json, out string? triggerFamily)
    {
        triggerFamily = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("investigationTasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind == JsonValueKind.Object
                    && task.TryGetProperty("triggerFamily", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    triggerFamily = value.GetString();
                    return !string.IsNullOrWhiteSpace(triggerFamily);
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool TryGetString(string? json, string propertyName, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static IReadOnlyList<string> GetStringArray(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryGetBoolean(string? json, string propertyName, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.GetBoolean();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
