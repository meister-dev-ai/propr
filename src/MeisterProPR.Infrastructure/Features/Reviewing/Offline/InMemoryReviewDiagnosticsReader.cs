// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory diagnostics reader for offline review execution.
/// </summary>
public sealed class InMemoryReviewDiagnosticsReader(InMemoryReviewJobRepository jobs) : IReviewDiagnosticsReader
{
    private const string InheritedEventTextOmittedMessage =
        "Inherited event payload omitted from this view to keep large same-revision retry traces responsive. Open the source job protocol to inspect the original captured body.";

    private const string EventTextOmittedMessage =
        "Event payload omitted from the overview to keep large protocol traces responsive. Select this pass to load the full captured body.";

    public Task<GetReviewJobProtocolResult?> GetJobProtocolAsync(
        Guid jobId,
        bool includeEvents = true,
        CancellationToken ct = default)
    {
        var job = jobs.GetById(jobId);
        if (job is null)
        {
            return Task.FromResult<GetReviewJobProtocolResult?>(null);
        }

        var protocols = new List<ReviewJobProtocolDto>(job.Protocols.Count + job.FileReviewResults.Count);
        protocols.AddRange(job.Protocols.Select(protocol => CreateProtocolDto(job, protocol, includeEvents: includeEvents)));

        foreach (var resumedFileResult in job.FileReviewResults.Where(result =>
                     result.IsComplete
                     && !result.IsFailed
                     && !result.IsExcluded
                     && !result.IsCarriedForward
                     && result.ResumedFromJobId.HasValue
                     && result.ResumedFromFileResultId.HasValue))
        {
            var sourceJobId = resumedFileResult.ResumedFromJobId!.Value;
            var sourceFileResultId = resumedFileResult.ResumedFromFileResultId!.Value;
            var sourceJob = jobs.GetById(sourceJobId);
            if (sourceJob is null)
            {
                continue;
            }

            var sourceProtocol = sourceJob.Protocols
                .Where(protocol => protocol.FileResultId == sourceFileResultId)
                .OrderByDescending(protocol => protocol.CompletedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(protocol => protocol.StartedAt)
                .FirstOrDefault(protocol => string.Equals(protocol.Outcome, "Completed", StringComparison.OrdinalIgnoreCase));

            if (sourceProtocol is null)
            {
                continue;
            }

            protocols.Add(
                CreateProtocolDto(
                    job,
                    sourceProtocol,
                    resumedFileResult,
                    includeEvents,
                    new ProtocolInheritanceDto(
                        sourceJobId,
                        sourceFileResultId,
                        sourceProtocol.Id,
                        sourceProtocol.CompletedAt)));
        }

        return Task.FromResult<GetReviewJobProtocolResult?>(new GetReviewJobProtocolResult(job.ClientId, protocols.AsReadOnly()));
    }

    public Task<ReviewJobProtocolDto?> GetJobProtocolPassAsync(Guid jobId, Guid protocolId, CancellationToken ct = default)
    {
        var job = jobs.GetById(jobId);
        if (job is null)
        {
            return Task.FromResult<ReviewJobProtocolDto?>(null);
        }

        var protocol = job.Protocols.FirstOrDefault(candidate => candidate.Id == protocolId);
        if (protocol is not null)
        {
            return Task.FromResult<ReviewJobProtocolDto?>(CreateProtocolDto(job, protocol, includeEvents: true));
        }

        foreach (var resumedFileResult in job.FileReviewResults.Where(result =>
                     result.IsComplete
                     && !result.IsFailed
                     && !result.IsExcluded
                     && !result.IsCarriedForward
                     && result.ResumedFromJobId.HasValue
                     && result.ResumedFromFileResultId.HasValue))
        {
            var sourceJob = jobs.GetById(resumedFileResult.ResumedFromJobId!.Value);
            var sourceProtocol = sourceJob?.Protocols.FirstOrDefault(candidate => candidate.Id == protocolId);
            if (sourceProtocol is null)
            {
                continue;
            }

            return Task.FromResult<ReviewJobProtocolDto?>(
                CreateProtocolDto(
                    job,
                    sourceProtocol,
                    resumedFileResult,
                    true,
                    new ProtocolInheritanceDto(
                        sourceJob!.Id,
                        resumedFileResult.ResumedFromFileResultId,
                        sourceProtocol.Id,
                        sourceProtocol.CompletedAt)));
        }

        return Task.FromResult<ReviewJobProtocolDto?>(null);
    }

    private static ReviewJobProtocolDto CreateProtocolDto(
        ReviewJob projectionJob,
        ReviewJobProtocol protocol,
        ReviewFileResult? fileResultOverride = null,
        bool includeEvents = true,
        ProtocolInheritanceDto? inheritance = null)
    {
        return new ReviewJobProtocolDto(
            protocol.Id,
            projectionJob.Id,
            protocol.AttemptNumber,
            protocol.Label,
            fileResultOverride?.Id ?? protocol.FileResultId,
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
            ResolveFinalSummary(projectionJob, protocol, fileResultOverride),
            ResolveFinalComments(projectionJob, protocol, fileResultOverride),
            CreateEventDtos(protocol, includeEvents, inheritance is not null))
        {
            Provider = projectionJob.Provider,
            ProviderScopePath = projectionJob.OrganizationUrl,
            ProviderProjectKey = ResolveProviderProjectKey(projectionJob),
            RepositoryId = projectionJob.RepositoryId,
            PullRequestId = projectionJob.PullRequestId,
            ResolvedReviewStrategy = projectionJob.ReviewStrategy,
            StrategySelectionSource = projectionJob.ReviewStrategySelectionSource,
            FileOutcome = ResolveFileOutcome(projectionJob, protocol, fileResultOverride),
            FollowUp = ResolveFollowUp(protocol),
            RepeatedJudgment = ResolveRepeatedJudgment(protocol),
            TotalCachedInputTokens = protocol.TotalCachedInputTokens,
            CacheObservability = protocol.CacheObservability,
            IsInherited = inheritance is not null,
            Inheritance = inheritance,
        };
    }

    private static IReadOnlyList<ProtocolEventDto> CreateEventDtos(
        ReviewJobProtocol protocol,
        bool includeEvents,
        bool isInherited)
    {
        if (!includeEvents)
        {
            return protocol.Events
                .Select(e => CreateEventDto(e, null, null, BuildOverviewOutputSummary(e, isInherited)))
                .ToList()
                .AsReadOnly();
        }

        return protocol.Events
            .Select(e => CreateEventDto(
                e,
                isInherited ? null : e.InputTextSample,
                isInherited ? null : e.SystemPrompt,
                isInherited ? BuildInheritedOutputSummary(e) : e.OutputSummary))
            .ToList()
            .AsReadOnly();
    }

    private static ProtocolEventDto CreateEventDto(
        ProtocolEvent e,
        string? inputTextSample,
        string? systemPrompt,
        string? outputSummary)
    {
        return new ProtocolEventDto(
            e.Id,
            e.Kind,
            e.Name,
            e.OccurredAt,
            e.InputTokens,
            e.OutputTokens,
            inputTextSample,
            systemPrompt,
            outputSummary,
            TraceSearchSupport.NormalizeEventCategory(e.EventCategory)
            ?? TraceSearchSupport.DeriveEventCategory(e.Kind, e.Name),
            e.Error)
        {
            CachedInputTokens = e.CachedInputTokens,
            CacheStatus = e.CacheStatus,
            CacheMissCategory = e.CacheMissCategory,
            PrefixEligibility = e.PrefixEligibility,
            ToolEvidence = CreateToolEvidenceDto(e),
            FinalizationAttemptKind = e.FinalizationAttemptKind,
            FinalizationReason = e.FinalizationReason,
            FinalizationOutcome = e.FinalizationOutcome,
            StartedAt = e.StartedAt,
            CompletedAt = e.CompletedAt,
            DurationMs = e.DurationMs,
            WaitDurationMs = e.WaitDurationMs,
            ActiveDurationMs = e.ActiveDurationMs,
            TimingAvailability = e.TimingAvailability,
            ToolOutcome = e.ToolOutcome,
            PhaseTimings = CreatePhaseTimingDtos(e.PhaseTimings),
        };
    }

    private static ProtocolToolEvidenceDto? CreateToolEvidenceDto(ProtocolEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.ToolEvidenceAction) || string.IsNullOrWhiteSpace(e.ToolEvidenceSourceToolName))
        {
            return null;
        }

        return new ProtocolToolEvidenceDto(
            e.ToolEvidenceSourceToolName,
            e.ToolEvidenceOriginalPayloadTokens ?? 0,
            e.ToolEvidenceBoundedPayloadTokens ?? 0,
            e.ToolEvidenceAction,
            e.ToolEvidenceRefreshable ?? false);
    }

    private static IReadOnlyList<ProtocolEventPhaseTimingDto>? CreatePhaseTimingDtos(IReadOnlyList<ProtocolEventPhaseTiming>? phaseTimings)
    {
        if (phaseTimings is not { Count: > 0 })
        {
            return null;
        }

        return phaseTimings
            .Select(phase => new ProtocolEventPhaseTimingDto(
                phase.Name,
                phase.DisplayName,
                phase.Sequence,
                phase.Occurrence,
                phase.StartedAt,
                phase.CompletedAt,
                phase.DurationMs,
                phase.Availability,
                phase.Outcome,
                phase.Summary))
            .ToList()
            .AsReadOnly();
    }

    private static string? BuildOverviewOutputSummary(ProtocolEvent e, bool isInherited)
    {
        if (!string.IsNullOrWhiteSpace(e.Error))
        {
            return e.OutputSummary;
        }

        if (e.OutputSummary is null)
        {
            return null;
        }

        return isInherited ? InheritedEventTextOmittedMessage : EventTextOmittedMessage;
    }

    private static string? BuildInheritedOutputSummary(ProtocolEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.Error))
        {
            return e.OutputSummary;
        }

        return e.OutputSummary is null
            ? null
            : InheritedEventTextOmittedMessage;
    }

    private static string? ResolveFinalSummary(ReviewJob job, ReviewJobProtocol protocol, ReviewFileResult? fileResultOverride = null)
    {
        if (protocol.FileResultId.HasValue)
        {
            return fileResultOverride is not null
                ? fileResultOverride.PerFileSummary
                : job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value)?.PerFileSummary;
        }

        if (string.Equals(protocol.Label, "synthesis", StringComparison.OrdinalIgnoreCase))
        {
            return job.Result?.Summary;
        }

        return null;
    }

    private static IReadOnlyList<ProtocolReviewCommentDto>? ResolveFinalComments(
        ReviewJob job,
        ReviewJobProtocol protocol,
        ReviewFileResult? fileResultOverride = null)
    {
        if (protocol.FileResultId.HasValue)
        {
            return (fileResultOverride
                    ?? job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value))?
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

    private static ProtocolFileOutcomeDto? ResolveFileOutcome(
        ReviewJob job,
        ReviewJobProtocol protocol,
        ReviewFileResult? fileResultOverride = null)
    {
        if (!protocol.FileResultId.HasValue)
        {
            return null;
        }

        var fileResult = fileResultOverride ?? job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value);
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
