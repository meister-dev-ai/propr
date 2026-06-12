// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

/// <summary>
///     EF-backed Reviewing diagnostics reader.
/// </summary>
public sealed class EfReviewDiagnosticsReader(
    IJobRepository jobRepository,
    IDbContextFactory<MeisterProPRDbContext>? dbContextFactory = null) : IReviewDiagnosticsReader
{
    private const string InheritedEventTextOmittedMessage =
        "Inherited event payload omitted from this view to keep large same-revision retry traces responsive. Open the source job protocol to inspect the original captured body.";

    private const string EventTextOmittedMessage =
        "Event payload omitted from the overview to keep large protocol traces responsive. Select this pass to load the full captured body.";

    public async Task<GetReviewJobProtocolResult?> GetJobProtocolAsync(
        Guid jobId,
        bool includeEvents = true,
        CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdWithProtocolsAsync(jobId, ct);

        if (job is null)
        {
            return null;
        }

        var protocols = new List<ReviewJobProtocolDto>(job.Protocols.Count + job.FileReviewResults.Count);

        protocols.AddRange(job.Protocols.Select(protocol => CreateProtocolDto(job, protocol, includeEvents: includeEvents)));

        var inheritedProtocols = await this.LoadInheritedProtocolsAsync(job, includeEvents, ct);
        protocols.AddRange(inheritedProtocols);

        return new GetReviewJobProtocolResult(job.ClientId, protocols.AsReadOnly());
    }

    public async Task<ReviewJobProtocolDto?> GetJobProtocolPassAsync(Guid jobId, Guid protocolId, CancellationToken ct = default)
    {
        var job = await jobRepository.GetByIdWithFileResultsAsync(jobId, ct);
        if (job is null)
        {
            return null;
        }

        if (dbContextFactory is null)
        {
            var fallbackJob = await jobRepository.GetByIdWithProtocolsAsync(jobId, ct);
            if (fallbackJob is null)
            {
                return null;
            }

            var protocol = fallbackJob.Protocols.FirstOrDefault(candidate => candidate.Id == protocolId);
            if (protocol is not null)
            {
                return CreateProtocolDto(fallbackJob, protocol, includeEvents: true);
            }

            return await this.LoadInheritedProtocolPassFallbackAsync(fallbackJob, protocolId, ct);
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var protocolEntity = await db.ReviewJobProtocols
            .AsNoTracking()
            .Where(protocol => protocol.JobId == jobId && protocol.Id == protocolId)
            .Include(protocol => protocol.Events.OrderBy(evt => evt.OccurredAt))
            .FirstOrDefaultAsync(ct);

        if (protocolEntity is not null)
        {
            return CreateProtocolDto(job, protocolEntity, includeEvents: true);
        }

        return await this.LoadInheritedProtocolPassFromStoreAsync(job, protocolId, ct);
    }

    private async Task<IReadOnlyList<ReviewJobProtocolDto>> LoadInheritedProtocolsAsync(ReviewJob job, bool includeEvents, CancellationToken ct)
    {
        var resumedFileResults = job.FileReviewResults
            .Where(result => result.IsComplete
                             && !result.IsFailed
                             && !result.IsExcluded
                             && !result.IsCarriedForward
                             && result.ResumedFromJobId.HasValue
                             && result.ResumedFromFileResultId.HasValue)
            .ToList();

        if (resumedFileResults.Count == 0)
        {
            return [];
        }

        var inheritedProtocols = new List<ReviewJobProtocolDto>(resumedFileResults.Count);
        var sourceProtocols = await this.LoadInheritedSourceProtocolsAsync(resumedFileResults, ct);

        foreach (var resumedFileResult in resumedFileResults)
        {
            var sourceJobId = resumedFileResult.ResumedFromJobId!.Value;
            var sourceFileResultId = resumedFileResult.ResumedFromFileResultId!.Value;
            if (!sourceProtocols.TryGetValue((sourceJobId, sourceFileResultId), out var sourceProtocol))
            {
                continue;
            }

            inheritedProtocols.Add(
                CreateProtocolDto(
                    job,
                    sourceProtocol,
                    resumedFileResult,
                    includeEvents,
                    new ProtocolInheritanceDto(
                        sourceJobId,
                        resumedFileResult.ResumedFromFileResultId,
                        sourceProtocol.Id,
                        sourceProtocol.CompletedAt)));
        }

        return inheritedProtocols;
    }

    private async Task<ReviewJobProtocolDto?> LoadInheritedProtocolPassFromStoreAsync(
        ReviewJob job,
        Guid protocolId,
        CancellationToken ct)
    {
        var resumedFileResult = job.FileReviewResults.FirstOrDefault(result =>
            result.IsComplete
            && !result.IsFailed
            && !result.IsExcluded
            && !result.IsCarriedForward
            && result.ResumedFromJobId.HasValue
            && result.ResumedFromFileResultId.HasValue);

        if (resumedFileResult is null || dbContextFactory is null)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var sourceProtocol = await db.ReviewJobProtocols
            .AsNoTracking()
            .Where(candidate => candidate.Id == protocolId)
            .Include(candidate => candidate.Events.OrderBy(evt => evt.OccurredAt))
            .FirstOrDefaultAsync(ct);

        if (sourceProtocol is null)
        {
            return null;
        }

        var matchingFileResult = job.FileReviewResults.FirstOrDefault(result =>
            result.ResumedFromJobId == sourceProtocol.JobId
            && result.ResumedFromFileResultId == sourceProtocol.FileResultId);

        if (matchingFileResult is null)
        {
            return null;
        }

        return CreateProtocolDto(
            job,
            sourceProtocol,
            matchingFileResult,
            true,
            new ProtocolInheritanceDto(
                sourceProtocol.JobId,
                matchingFileResult.ResumedFromFileResultId,
                sourceProtocol.Id,
                sourceProtocol.CompletedAt));
    }

    private async Task<ReviewJobProtocolDto?> LoadInheritedProtocolPassFallbackAsync(
        ReviewJob job,
        Guid protocolId,
        CancellationToken ct)
    {
        var resumedFileResults = job.FileReviewResults.Where(result =>
                result.IsComplete
                && !result.IsFailed
                && !result.IsExcluded
                && !result.IsCarriedForward
                && result.ResumedFromJobId.HasValue
                && result.ResumedFromFileResultId.HasValue)
            .ToList();

        foreach (var resumedFileResult in resumedFileResults)
        {
            var sourceJob = await jobRepository.GetByIdWithProtocolsAsync(resumedFileResult.ResumedFromJobId!.Value, ct);
            if (sourceJob is null)
            {
                continue;
            }

            var sourceProtocol = sourceJob.Protocols.FirstOrDefault(candidate => candidate.Id == protocolId);
            if (sourceProtocol is null)
            {
                continue;
            }

            return CreateProtocolDto(
                job,
                sourceProtocol,
                resumedFileResult,
                true,
                new ProtocolInheritanceDto(
                    sourceJob.Id,
                    resumedFileResult.ResumedFromFileResultId,
                    sourceProtocol.Id,
                    sourceProtocol.CompletedAt));
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<(Guid SourceJobId, Guid SourceFileResultId), ReviewJobProtocol>> LoadInheritedSourceProtocolsAsync(
        IReadOnlyList<ReviewFileResult> resumedFileResults,
        CancellationToken ct)
    {
        return dbContextFactory is null
            ? await this.LoadInheritedSourceProtocolsFromRepositoryAsync(resumedFileResults, ct)
            : await this.LoadInheritedSourceProtocolsFromStoreAsync(resumedFileResults, ct);
    }

    private async Task<IReadOnlyDictionary<(Guid SourceJobId, Guid SourceFileResultId), ReviewJobProtocol>> LoadInheritedSourceProtocolsFromStoreAsync(
        IReadOnlyList<ReviewFileResult> resumedFileResults,
        CancellationToken ct)
    {
        await using var db = await dbContextFactory!.CreateDbContextAsync(ct);
        var sourceProtocols = new Dictionary<(Guid SourceJobId, Guid SourceFileResultId), ReviewJobProtocol>();

        foreach (var sourceJobGroup in resumedFileResults.GroupBy(result => result.ResumedFromJobId!.Value))
        {
            var sourceFileResultIds = sourceJobGroup
                .Select(result => result.ResumedFromFileResultId!.Value)
                .Distinct()
                .ToArray();

            var selectedProtocolIds = (await db.ReviewJobProtocols
                    .AsNoTracking()
                    .Where(protocol => protocol.JobId == sourceJobGroup.Key
                                       && protocol.FileResultId.HasValue
                                       && sourceFileResultIds.Contains(protocol.FileResultId.Value))
                    .Select(protocol => new
                    {
                        protocol.Id,
                        FileResultId = protocol.FileResultId!.Value,
                        protocol.CompletedAt,
                        protocol.StartedAt,
                        protocol.Outcome,
                    })
                    .ToListAsync(ct))
                .Where(protocol => string.Equals(protocol.Outcome, "Completed", StringComparison.OrdinalIgnoreCase))
                .GroupBy(protocol => protocol.FileResultId)
                .Select(group => group
                    .OrderByDescending(protocol => protocol.CompletedAt ?? DateTimeOffset.MinValue)
                    .ThenByDescending(protocol => protocol.StartedAt)
                    .Select(protocol => protocol.Id)
                    .First())
                .ToArray();

            if (selectedProtocolIds.Length == 0)
            {
                continue;
            }

            var selectedProtocolsProj = await db.ReviewJobProtocols
                .AsNoTracking()
                .Where(protocol => selectedProtocolIds.Contains(protocol.Id))
                .Select(protocol => new
                {
                    Protocol = protocol,
                    Events = protocol.Events
                        .OrderBy(evt => evt.OccurredAt)
                        .Select(evt => new
                        {
                            evt.Id,
                            evt.ProtocolId,
                            evt.Kind,
                            evt.Name,
                            evt.OccurredAt,
                            evt.InputTokens,
                            evt.OutputTokens,
                            evt.CachedInputTokens,
                            evt.CacheStatus,
                            evt.CacheMissCategory,
                            evt.PrefixEligibility,
                            evt.ToolEvidenceAction,
                            evt.ToolEvidenceSourceToolName,
                            evt.ToolEvidenceOriginalPayloadTokens,
                            evt.ToolEvidenceBoundedPayloadTokens,
                            evt.ToolEvidenceRefreshable,
                            evt.FinalizationAttemptKind,
                            evt.FinalizationReason,
                            evt.FinalizationOutcome,
                            evt.StartedAt,
                            evt.CompletedAt,
                            evt.DurationMs,
                            evt.WaitDurationMs,
                            evt.ActiveDurationMs,
                            evt.TimingAvailability,
                            evt.ToolOutcome,
                            evt.PhaseTimings,
                            evt.EventCategory,
                            evt.Error,
                            evt.OutputSummary,
                        })
                        .ToList(),
                })
                .ToListAsync(ct);

            foreach (var proj in selectedProtocolsProj)
            {
                var protocol = proj.Protocol;
                foreach (var evtProj in proj.Events)
                {
                    var evt = new ProtocolEvent
                    {
                        Id = evtProj.Id,
                        ProtocolId = evtProj.ProtocolId,
                        Kind = evtProj.Kind,
                        Name = evtProj.Name,
                        OccurredAt = evtProj.OccurredAt,
                        InputTokens = evtProj.InputTokens,
                        OutputTokens = evtProj.OutputTokens,
                        CachedInputTokens = evtProj.CachedInputTokens,
                        CacheStatus = evtProj.CacheStatus,
                        CacheMissCategory = evtProj.CacheMissCategory,
                        PrefixEligibility = evtProj.PrefixEligibility,
                        ToolEvidenceAction = evtProj.ToolEvidenceAction,
                        ToolEvidenceSourceToolName = evtProj.ToolEvidenceSourceToolName,
                        ToolEvidenceOriginalPayloadTokens = evtProj.ToolEvidenceOriginalPayloadTokens,
                        ToolEvidenceBoundedPayloadTokens = evtProj.ToolEvidenceBoundedPayloadTokens,
                        ToolEvidenceRefreshable = evtProj.ToolEvidenceRefreshable,
                        FinalizationAttemptKind = evtProj.FinalizationAttemptKind,
                        FinalizationReason = evtProj.FinalizationReason,
                        FinalizationOutcome = evtProj.FinalizationOutcome,
                        StartedAt = evtProj.StartedAt,
                        CompletedAt = evtProj.CompletedAt,
                        DurationMs = evtProj.DurationMs,
                        WaitDurationMs = evtProj.WaitDurationMs,
                        ActiveDurationMs = evtProj.ActiveDurationMs,
                        TimingAvailability = evtProj.TimingAvailability,
                        ToolOutcome = evtProj.ToolOutcome,
                        PhaseTimings = evtProj.PhaseTimings,
                        EventCategory = evtProj.EventCategory,
                        Error = evtProj.Error,
                        OutputSummary = evtProj.OutputSummary,
                        InputTextSample = null,
                        SystemPrompt = null,
                    };
                    protocol.Events.Add(evt);
                }

                if (protocol.FileResultId.HasValue)
                {
                    sourceProtocols[(sourceJobGroup.Key, protocol.FileResultId.Value)] = protocol;
                }
            }
        }

        return sourceProtocols;
    }

    private async Task<IReadOnlyDictionary<(Guid SourceJobId, Guid SourceFileResultId), ReviewJobProtocol>> LoadInheritedSourceProtocolsFromRepositoryAsync(
        IReadOnlyList<ReviewFileResult> resumedFileResults,
        CancellationToken ct)
    {
        var sourceProtocols = new Dictionary<(Guid SourceJobId, Guid SourceFileResultId), ReviewJobProtocol>();
        var sourceJobs = new Dictionary<Guid, ReviewJob?>();

        foreach (var resumedFileResult in resumedFileResults)
        {
            var sourceJobId = resumedFileResult.ResumedFromJobId!.Value;
            if (!sourceJobs.TryGetValue(sourceJobId, out var sourceJob))
            {
                sourceJob = await jobRepository.GetByIdWithProtocolsAsync(sourceJobId, ct);
                sourceJobs[sourceJobId] = sourceJob;
            }

            if (sourceJob is null)
            {
                continue;
            }

            var sourceFileResultId = resumedFileResult.ResumedFromFileResultId!.Value;
            var sourceProtocol = ResolveInheritedSourceProtocol(sourceJob, sourceFileResultId);
            if (sourceProtocol is null)
            {
                continue;
            }

            sourceProtocols[(sourceJobId, sourceFileResultId)] = sourceProtocol;
        }

        return sourceProtocols;
    }

    private static ReviewJobProtocol? ResolveInheritedSourceProtocol(ReviewJob sourceJob, Guid sourceFileResultId)
    {
        return sourceJob.Protocols
            .Where(protocol => protocol.FileResultId == sourceFileResultId)
            .OrderByDescending(protocol => protocol.CompletedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(protocol => protocol.StartedAt)
            .FirstOrDefault(protocol => string.Equals(protocol.Outcome, "Completed", StringComparison.OrdinalIgnoreCase));
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
            ProRvPrefilter = ResolveProRvPrefilter(protocol),
            AgentSession = ResolveAgentSession(protocol),
            Workspace = ResolveWorkspace(protocol),
            TotalCachedInputTokens = protocol.TotalCachedInputTokens,
            CacheObservability = protocol.CacheObservability,
            IsInherited = inheritance is not null,
            Inheritance = inheritance,
        };
    }

    private static ProtocolWorkspaceDto? ResolveWorkspace(ReviewJobProtocol protocol)
    {
        var prepared = protocol.Events.FirstOrDefault(evt => string.Equals(evt.Name, "local_workspace_prepared", StringComparison.Ordinal));
        var failed = protocol.Events.FirstOrDefault(evt => string.Equals(evt.Name, "local_workspace_failed", StringComparison.Ordinal));
        var fallback = protocol.Events.FirstOrDefault(evt => string.Equals(evt.Name, "local_workspace_fallback_applied", StringComparison.Ordinal));
        if (prepared is null && failed is null && fallback is null)
        {
            return null;
        }

        return new ProtocolWorkspaceDto(
            true,
            prepared is not null,
            fallback is not null,
            TryGetWorkspaceString(prepared?.OutputSummary, "workspaceKey"),
            TryGetWorkspaceString(failed?.OutputSummary, "stage"),
            TryGetWorkspaceString(failed?.OutputSummary, "code"),
            TryGetWorkspaceString(failed?.OutputSummary, "message"));
    }

    private static string? TryGetWorkspaceString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
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
                ? fileResultOverride?.PerFileSummary
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
            return (fileResultOverride is not null
                    ? fileResultOverride
                    : job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value))?
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

        var fileResult = fileResultOverride is not null
            ? fileResultOverride
            : job.FileReviewResults.FirstOrDefault(result => result.Id == protocol.FileResultId.Value);
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

    private static ProtocolProRvPrefilterDto? ResolveProRvPrefilter(ReviewJobProtocol protocol)
    {
        const string notSelected = "not_selected";
        const string skipped = "skipped";
        const string completed = "completed";
        const string failed = "failed";

        var executionState = notSelected;
        string? stageId = null;
        string? reason = null;
        string? runtimeSource = null;
        string? modelId = null;
        string? language = null;
        string? prefilterStatus = null;
        var guidanceCount = 0;
        var selected = false;
        var aiCallRecorded = false;
        var guidanceApplied = false;
        string? appliedPromptKind = null;
        IReadOnlyList<string> appliedGuidanceIds = [];

        foreach (var evt in protocol.Events)
        {
            if (string.Equals(evt.Name, ReviewProtocolEventNames.ReviewPipelineProfileApplied, StringComparison.Ordinal))
            {
                if (GetStringArray(evt.OutputSummary, "dispatchStageIds").Contains(FileByFileProRvPrefilterStage.StageIdConstant, StringComparer.Ordinal) ||
                    GetStringArray(evt.OutputSummary, "dispatchStageIds").Contains(AgenticProRvPrefilterStage.StageIdConstant, StringComparer.Ordinal))
                {
                    selected = true;
                }
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterAiCall, StringComparison.Ordinal))
            {
                aiCallRecorded = true;
                if (TryGetString(evt.SystemPrompt, "modelId", out var aiCallModelId) && !string.IsNullOrWhiteSpace(aiCallModelId))
                {
                    modelId ??= aiCallModelId;
                }

                if (TryGetString(evt.SystemPrompt, "runtimeSource", out var aiCallRuntimeSource) && !string.IsNullOrWhiteSpace(aiCallRuntimeSource))
                {
                    runtimeSource ??= aiCallRuntimeSource;
                }

                if (TryGetString(evt.InputTextSample, "status", out var aiCallStatus) && !string.IsNullOrWhiteSpace(aiCallStatus))
                {
                    prefilterStatus ??= aiCallStatus;
                }

                if (TryGetString(evt.InputTextSample, "language", out var aiCallLanguage) && !string.IsNullOrWhiteSpace(aiCallLanguage))
                {
                    language ??= aiCallLanguage;
                }

                if (TryGetString(evt.InputTextSample, "stageId", out var aiCallStageId) && !string.IsNullOrWhiteSpace(aiCallStageId))
                {
                    stageId ??= aiCallStageId;
                }

                if (!string.IsNullOrWhiteSpace(evt.Error))
                {
                    reason ??= evt.Error;
                }
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.ProRVFocusedGuidanceApplied, StringComparison.Ordinal))
            {
                if (TryGetBoolean(evt.InputTextSample, "applied", out var applied))
                {
                    guidanceApplied = applied;
                }

                if (TryGetString(evt.InputTextSample, "promptKind", out var promptKind) && !string.IsNullOrWhiteSpace(promptKind))
                {
                    appliedPromptKind = promptKind;
                }

                var ids = GetStringArray(evt.OutputSummary, "guidanceIds");
                if (ids.Count > 0)
                {
                    appliedGuidanceIds = ids;
                }
            }

            if (!string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterStarted, StringComparison.Ordinal) &&
                !string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterSkipped, StringComparison.Ordinal) &&
                !string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterCompleted, StringComparison.Ordinal) &&
                !string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterFailed, StringComparison.Ordinal))
            {
                continue;
            }

            selected = true;
            if (TryGetString(evt.InputTextSample, "stageId", out var eventStageId) && !string.IsNullOrWhiteSpace(eventStageId))
            {
                stageId = eventStageId;
            }

            if (TryGetString(evt.OutputSummary, "runtimeSource", out var eventRuntimeSource) && !string.IsNullOrWhiteSpace(eventRuntimeSource))
            {
                runtimeSource = eventRuntimeSource;
            }

            if (TryGetString(evt.OutputSummary, "modelId", out var eventModelId) && !string.IsNullOrWhiteSpace(eventModelId))
            {
                modelId = eventModelId;
            }

            if (TryGetString(evt.OutputSummary, "language", out var eventLanguage) && !string.IsNullOrWhiteSpace(eventLanguage))
            {
                language = eventLanguage;
            }

            if (TryGetString(evt.OutputSummary, "proRvStatus", out var eventPrefilterStatus) && !string.IsNullOrWhiteSpace(eventPrefilterStatus))
            {
                prefilterStatus = eventPrefilterStatus;
            }

            if (TryGetInt32(evt.OutputSummary, "guidanceCount", out var parsedGuidanceCount))
            {
                guidanceCount = parsedGuidanceCount;
            }

            if (!string.IsNullOrWhiteSpace(evt.Error))
            {
                reason = evt.Error;
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterSkipped, StringComparison.Ordinal))
            {
                executionState = skipped;
                if (TryGetString(evt.OutputSummary, "reason", out var skipReason) && !string.IsNullOrWhiteSpace(skipReason))
                {
                    reason = skipReason;
                }
            }
            else if (string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterCompleted, StringComparison.Ordinal))
            {
                executionState = completed;
            }
            else if (string.Equals(evt.Name, ReviewProtocolEventNames.ProRVPrefilterFailed, StringComparison.Ordinal))
            {
                executionState = failed;
            }
        }

        return selected || aiCallRecorded
            ? new ProtocolProRvPrefilterDto(
                selected,
                executionState,
                stageId,
                reason,
                runtimeSource,
                modelId,
                language,
                prefilterStatus,
                guidanceCount,
                aiCallRecorded,
                guidanceApplied,
                appliedPromptKind,
                appliedGuidanceIds)
            : null;
    }

    private static ProtocolAgentSessionDto? ResolveAgentSession(ReviewJobProtocol protocol)
    {
        string? remoteConversationId = null;
        string? bindingMethod = null;
        string? bindingOutcome = null;
        string? promptMode = null;
        string? fallbackReason = null;
        var usedManagedRemoteConversation = false;
        var usedLocalReplay = false;

        foreach (var evt in protocol.Events)
        {
            if (string.Equals(evt.Name, ReviewProtocolEventNames.ReviewAgentSessionBinding, StringComparison.Ordinal))
            {
                if (TryGetString(evt.InputTextSample, "remoteConversationId", out var bindingConversationId))
                {
                    remoteConversationId ??= bindingConversationId;
                }

                if (TryGetString(evt.InputTextSample, "bindingMethod", out var parsedBindingMethod))
                {
                    bindingMethod ??= parsedBindingMethod;
                }

                if (TryGetString(evt.InputTextSample, "bindingOutcome", out var parsedBindingOutcome))
                {
                    bindingOutcome ??= parsedBindingOutcome;
                }

                if (TryGetString(evt.InputTextSample, "promptMode", out var parsedPromptMode))
                {
                    promptMode ??= parsedPromptMode;
                }

                usedManagedRemoteConversation = true;
                continue;
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.ReviewAgentSessionTurn, StringComparison.Ordinal))
            {
                if (TryGetString(evt.InputTextSample, "remoteConversationId", out var turnConversationId))
                {
                    remoteConversationId ??= turnConversationId;
                }

                if (TryGetString(evt.InputTextSample, "promptMode", out var parsedTurnPromptMode))
                {
                    promptMode ??= parsedTurnPromptMode;
                }

                if (TryGetBoolean(evt.InputTextSample, "usedRemoteConversation", out var parsedUsedRemoteConversation))
                {
                    usedManagedRemoteConversation |= parsedUsedRemoteConversation;
                }

                if (TryGetBoolean(evt.InputTextSample, "usedLocalReplay", out var parsedUsedLocalReplay))
                {
                    usedLocalReplay |= parsedUsedLocalReplay;
                }

                continue;
            }

            if (string.Equals(evt.Name, ReviewProtocolEventNames.ReviewAgentSessionFallback, StringComparison.Ordinal))
            {
                if (TryGetString(evt.InputTextSample, "reason", out var parsedFallbackReason))
                {
                    fallbackReason ??= parsedFallbackReason;
                }

                usedLocalReplay = true;
            }
        }

        return remoteConversationId is not null || bindingMethod is not null || fallbackReason is not null || usedManagedRemoteConversation || usedLocalReplay
            ? new ProtocolAgentSessionDto(
                usedManagedRemoteConversation,
                remoteConversationId,
                bindingMethod,
                bindingOutcome,
                promptMode,
                usedLocalReplay,
                fallbackReason)
            : null;
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

    private static bool TryGetInt32(string? json, string propertyName, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
