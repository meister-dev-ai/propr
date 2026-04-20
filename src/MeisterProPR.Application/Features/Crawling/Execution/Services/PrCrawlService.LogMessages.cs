// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Services;

public sealed partial class PrCrawlService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fetch assigned PRs for {OrgUrl}/{ProjectId}")]
    private static partial void LogConfigFetchError(ILogger logger, string orgUrl, string projectId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR crawl started. Active configurations: {Count}")]
    private static partial void LogCrawlStarted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job already exists for PR #{PrId} iteration {IterationId}: {JobId}")]
    private static partial void LogJobAlreadyExists(ILogger logger, int prId, int iterationId, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Created new review job {JobId} for PR #{PrId} iteration {IterationId}")]
    private static partial void LogJobCreated(ILogger logger, Guid jobId, int prId, int iterationId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "Skipping review job creation for PR #{PrId} iteration {IterationId} because no code or reviewer-thread changes were detected since the last completed review")]
    private static partial void LogSkippedNoReviewChanges(ILogger logger, int prId, int iterationId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "Re-evaluating PR #{PrId} iteration {IterationId} because reviewer-thread activity changed without a new code iteration")]
    private static partial void LogSameIterationThreadChangeDetected(ILogger logger, int prId, int iterationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Failed to evaluate whether PR #{PrId} iteration {IterationId} needs a new review job. Falling back to enqueue for safety.")]
    private static partial void LogEnqueueDecisionFailed(ILogger logger, int prId, int iterationId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Shared pull-request synchronization failed for PR #{PrId} in {OrgUrl}/{ProjectId} during {SummaryLabel}")]
    private static partial void LogSynchronizationFailed(
        ILogger logger,
        int prId,
        string orgUrl,
        string projectId,
        string summaryLabel,
        Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Discovered {Count} assigned PRs in {OrgUrl}/{ProjectId}")]
    private static partial void LogPrsDiscovered(ILogger logger, int count, string orgUrl, string projectId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "Abandonment check: active job {JobId} for PR #{PrId} is not in discovered list — fetching live status")]
    private static partial void LogAbandonmentCheckStarted(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(Level = LogLevel.Information, Message = "PR #{PrId} is abandoned — cancelling review job {JobId}")]
    private static partial void LogJobCancelledForAbandonedPr(ILogger logger, int prId, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Thread memory state machine disabled for {OrgUrl}/{ProjectId}: threadStatusFetcher missing={FetcherMissing}, threadMemoryService missing={MemoryMissing}, prScanRepository missing={ScanRepoMissing}. Ensure AI_EMBEDDING_ENDPOINT, AI_EMBEDDING_DEPLOYMENT, and real ADO mode are configured.")]
    private static partial void LogStateMachineServicesUnavailable(
        ILogger logger,
        string orgUrl,
        string projectId,
        bool fetcherMissing,
        bool memoryMissing,
        bool scanRepoMissing);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Thread memory state machine skipped for {OrgUrl}/{ProjectId}: reviewer identity not configured on client. Set reviewer_id on the client record to enable thread memory.")]
    private static partial void LogStateMachineSkippedNoReviewerId(ILogger logger, string orgUrl, string projectId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Thread memory state machine skipped for PR #{PrId} (client {ClientId}): no scan baseline yet — will activate after first review job for this PR")]
    private static partial void LogStateMachineSkippedNoScan(ILogger logger, int prId, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Thread memory state machine evaluating {ThreadCount} threads for PR #{PrId}")]
    private static partial void LogStateMachineEvaluating(ILogger logger, int prId, int threadCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message =
            "Thread memory state machine no-op for PR #{PrId} thread {ThreadId} (previous={PreviousStatus}, current={CurrentStatus})")]
    private static partial void LogStateMachineNoOp(
        ILogger logger,
        int prId,
        int threadId,
        string? previousStatus,
        string currentStatus);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Skipping crawl configuration {ConfigId} because {InvalidCount} selected ProCursor source associations are invalid")]
    private static partial void LogSkippedInvalidSourceScope(ILogger logger, Guid configId, int invalidCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Skipping crawl configuration {ConfigId} because it uses selected ProCursor sources but none remain eligible")]
    private static partial void LogSkippedEmptySourceScope(ILogger logger, Guid configId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Snapshotted {SourceCount} selected ProCursor sources from crawl configuration {ConfigId} onto review job {JobId}")]
    private static partial void LogSelectedSourceScopeSnapshotted(
        ILogger logger,
        Guid configId,
        Guid jobId,
        int sourceCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Thread memory state machine failed for PR #{PrId} (client {ClientId})")]
    private static partial void LogStateMachineFailed(ILogger logger, int prId, Guid clientId, Exception ex);
}
