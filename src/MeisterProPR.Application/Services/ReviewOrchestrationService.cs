// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Features.Reviewing.Execution;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Support;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates the end-to-end process of handling a review job.
/// </summary>
public sealed partial class ReviewOrchestrationService(
    IReviewJobExecutionStore jobs,
    IPullRequestFetcher prFetcher,
    IScmProviderRegistry providerRegistry,
    IClientRegistry clientRegistry,
    IReviewPrScanRepository prScanRepository,
    IAiCommentResolutionCore resolutionCore,
    IProtocolRecorder protocolRecorder,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IOptions<AiReviewOptions> options,
    ILogger<ReviewOrchestrationService> logger,
    IAiConnectionRepository aiConnectionRepository,
    IAiChatClientFactory aiChatClientFactory,
    IFileByFileReviewOrchestrator fileByFileReviewOrchestrator,
    IPromptOverrideService? promptOverrideService = null,
    IProviderActivationService? providerActivationService = null,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    IReviewRepositoryWorkspaceManager? workspaceManager = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IReviewArchiveIngestionService? reviewArchiveIngestionService = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IBudgetCapsProvider? budgetCapsProvider = null,
    IReviewSpendAccumulator? spendAccumulator = null,
    IBudgetScopeAccessor? budgetScopeAccessor = null) : IReviewJobProcessor
{
    private const string LocalWorkspacePreparedEventName = "local_workspace_prepared";
    private const string LocalWorkspaceFailedEventName = "local_workspace_failed";
    private const string LocalWorkspaceFallbackAppliedEventName = "local_workspace_fallback_applied";

    private readonly AiReviewOptions _opts = options.Value;

    /// <summary>Processes the given review job end-to-end.</summary>
    public async Task ProcessAsync(ReviewJob job, CancellationToken ct)
    {
        if (providerActivationService is not null && !await providerActivationService.IsEnabledAsync(job.Provider, ct))
        {
            await jobs.SetFailedAsync(
                job.Id,
                "The provider family is currently disabled by system administration.",
                ct);
            return;
        }

        var reviewerContext = await this.ResolveReviewerAsync(job, ct);

        var resolvedReviewRuntime = await this.ResolveAiConnectionAsync(job, ct);
        if (resolvedReviewRuntime is null)
        {
            return;
        }

        var budgetScope = await this.TryCreateBudgetScopeAsync(job, ct);
        using var budgetScopeHandle = budgetScope is null ? null : budgetScopeAccessor!.BeginScope(budgetScope);

        PullRequest? pr = null;

        try
        {
            pr = await this.RunReviewPipelineAsync(
                job,
                reviewerContext.EffectiveReviewerId,
                reviewerContext.ConfiguredTriggerReviewer,
                resolvedReviewRuntime.Value.ChatClient,
                resolvedReviewRuntime.Value.Capabilities,
                ct);
        }
        catch (BudgetHardCapReachedException ex)
        {
            await this.HandleBudgetCutAsync(job, ex.Breach, ct);
            return;
        }
        catch (PartialReviewFailureException ex)
        {
            // A hard-cap trip can surface wrapped as a partial failure; treat it as budget-exceeded if the scope
            // tripped, otherwise fall back to the normal partial-failure handling.
            if (await this.TryHandleBudgetCutAsync(job, budgetScope, ct))
            {
                return;
            }

            await this.HandlePartialReviewFailureAsync(job, pr, ex, ct);
            return;
        }
        catch (Exception ex)
        {
            if (await this.TryHandleBudgetCutAsync(job, budgetScope, ct))
            {
                return;
            }

            LogReviewFailed(logger, job.Id, ex);
            await jobs.SetFailedAsync(job.Id, ex.Message, ct);
            return;
        }

        if (pr is not null)
        {
            await this.SaveScanAsync(
                job,
                GetReviewerThreads(pr, reviewerContext.EffectiveReviewerId),
                reviewerContext.EffectiveReviewerId,
                pr.AuthorizedIdentityName,
                pr.AuthorizedIdentityId,
                ct);
        }
    }

    private async Task<BudgetScope?> TryCreateBudgetScopeAsync(ReviewJob job, CancellationToken ct)
    {
        if (budgetScopeAccessor is null || budgetCapsProvider is null || spendAccumulator is null)
        {
            return null;
        }

        var caps = await budgetCapsProvider.GetCapsAsync(job.ClientId, ct);
        if (!caps.AnyConfigured)
        {
            return null;
        }

        var baseline = await spendAccumulator.GetBaselineAsync(job, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        return new BudgetScope(caps, baseline);
    }

    private async Task<bool> TryHandleBudgetCutAsync(ReviewJob job, BudgetScope? budgetScope, CancellationToken ct)
    {
        if (budgetScope?.TrippedBreach is not { } breach)
        {
            return false;
        }

        await this.HandleBudgetCutAsync(job, breach, ct);
        return true;
    }

    private async Task HandleBudgetCutAsync(ReviewJob job, BudgetBreach breach, CancellationToken ct)
    {
        LogBudgetHardCapReached(logger, job.Id, breach.Scope, breach.ThresholdUsd, breach.SpentUsd);
        await jobs.SetBudgetExceededAsync(job.Id, breach.Scope, breach.CapKind, breach.ThresholdUsd, breach.SpentUsd, ct);
    }

    private async Task<PullRequest?> RunReviewPipelineAsync(
        ReviewJob job,
        Guid? reviewerId,
        ReviewerIdentity? reviewer,
        IChatClient overrideChatClient,
        AgentReviewRuntimeCapabilities runtimeCapabilities,
        CancellationToken ct)
    {
        LogReviewStarted(logger, job.Id, job.PullRequestId);

        var (scan, isNewIteration, baselineJob, baselineIsFullCoverage, resumeJob, compareToIterationId, compareToReviewRevision) =
            await this.LoadScanStateAsync(job, ct);

        // Lightweight fetch: get branch names so the workspace can be prepared before the
        // full content fetch — avoids N GetItemAsync calls for ADO-backed reviews.
        var prRef = await this.FetchPullRequestRefAsync(job, ct);

        // Prepare workspace early using branch names; full content fetch uses it below.
        var workspacePreparation = await this.PrepareWorkspaceForFetchAsync(job, prRef, ct);
        var earlyWorkspace = workspacePreparation.Workspace;

        var pr = await this.TryFetchPullRequestWithCleanupAsync(
            job,
            compareToIterationId,
            compareToReviewRevision,
            earlyWorkspace,
            workspacePreparation,
            ct);
        if (pr is null)
        {
            return null;
        }

        var reviewerThreads = GetReviewerThreads(pr, reviewerId);
        var providerCapabilities = providerRegistry.GetRegisteredCapabilities(job.Provider) ?? [];

        if (!isNewIteration && !HasNewThreadReplies(reviewerThreads, scan!, reviewerId, pr.AuthorizedIdentityId, pr.AuthorizedIdentityName))
        {
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                pr,
                reviewerId,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedNoChange(logger, job.Id, job.PullRequestId),
                ct);
        }

        await this.AddOptionalReviewerIfSupportedAsync(job, reviewer, providerCapabilities, ct);

        await this.EvaluateExistingThreadsAsync(
            job,
            pr,
            reviewerThreads,
            scan,
            isNewIteration,
            reviewerId,
            providerCapabilities,
            overrideChatClient,
            ct);

        if (!isNewIteration)
        {
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                pr,
                reviewerId,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedNoChange(logger, job.Id, job.PullRequestId),
                ct);
        }

        var (systemContext, carriedForwardPaths) = await this.BuildReviewContextAsync(
            job,
            pr,
            baselineJob,
            baselineIsFullCoverage,
            resumeJob,
            overrideChatClient,
            runtimeCapabilities,
            workspacePreparation,
            ct);

        if (systemContext is null)
        {
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                pr,
                reviewerId,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedNoChange(logger, job.Id, job.PullRequestId),
                ct);
        }

        pr = await this.AttachLinkedItemsAsync(job, pr, systemContext, ct);

        if (this.IsJobStopped(job))
        {
            LogJobCancelledBeforeFileReview(logger, job.Id);
            return null;
        }

        var result = await this.DispatchFileReviewAsync(job, pr, systemContext, overrideChatClient, ct);

        if (this.IsJobStopped(job))
        {
            LogJobCancelledAfterFileReview(logger, job.Id);
            return null;
        }

        if (carriedForwardPaths.Count > 0)
        {
            result = result with { CarriedForwardFilePaths = carriedForwardPaths };
        }

        if (string.IsNullOrWhiteSpace(result.Summary) && result.Comments.Count == 0)
        {
            return await this.DisposeSkipAndFinalizeAsync(
                job,
                pr,
                reviewerId,
                earlyWorkspace,
                workspacePreparation,
                () => LogSkippedEmptyReview(logger, job.Id, job.PullRequestId),
                ct);
        }

        // Final status re-check immediately before the only step that posts to the provider. In a
        // multi-instance deployment a manual stop may land on another instance and never reach this
        // instance's cancellation token, so the persisted status is the last line of defence against
        // publishing the review of a job an administrator has stopped (or that was cancelled/superseded).
        if (this.IsJobStopped(job))
        {
            LogJobCancelledAfterFileReview(logger, job.Id);
            return null;
        }

        await this.PublishReviewResultAsync(job, pr, result, compareToIterationId, ct);

        await this.RetainIncrementDiffsAsync(job, pr, ct);

        return pr;
    }

    private async Task<PullRequest?> TryFetchPullRequestWithCleanupAsync(
        ReviewJob job,
        int? compareToIterationId,
        ReviewRevision? compareToReviewRevision,
        IReviewRepositoryWorkspace? earlyWorkspace,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        CancellationToken ct)
    {
        try
        {
            var pr = await this.FetchPullRequestAsync(
                job,
                compareToIterationId,
                compareToReviewRevision,
                earlyWorkspace,
                ct);
            if (pr is null)
            {
                await DisposeEarlyWorkspaceAsync(earlyWorkspace, workspacePreparation);
                return null;
            }

            return pr;
        }
        catch
        {
            await DisposeEarlyWorkspaceAsync(earlyWorkspace, workspacePreparation);
            throw;
        }
    }

    private async Task AddOptionalReviewerIfSupportedAsync(
        ReviewJob job,
        ReviewerIdentity? reviewer,
        IReadOnlyCollection<string> providerCapabilities,
        CancellationToken ct)
    {
        if (reviewer is null)
        {
            return;
        }

        if (!providerCapabilities.Any(capability => string.Equals(
                capability,
                "reviewAssignment",
                StringComparison.Ordinal)))
        {
            return;
        }

        await providerRegistry.GetReviewAssignmentService(job.Provider)
            .AddOptionalReviewerAsync(job.ClientId, job.CodeReviewReference, reviewer, ct);
    }

    private async Task<PullRequest?> DisposeSkipAndFinalizeAsync(
        ReviewJob job,
        PullRequest pr,
        Guid? reviewerId,
        IReviewRepositoryWorkspace? earlyWorkspace,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        Action logSkip,
        CancellationToken ct)
    {
        logSkip();
        await DisposeEarlyWorkspaceAsync(earlyWorkspace, workspacePreparation);
        await this.SaveScanAndDeleteJobAsync(job, pr, reviewerId, ct);
        return null;
    }

    private bool IsJobStopped(ReviewJob job)
    {
        return jobs.GetById(job.Id)?.Status is JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped or JobStatus.BudgetExceeded
            or JobStatus.BudgetHeld;
    }

    // Passive archive observer: when the producing connection opted in to diff retention, persist the
    // increment's per-file canonical unified diffs into the review-archive store. This runs after the
    // review is otherwise complete and decided. It never alters review behavior, deduplication, memory,
    // or the scope snapshot; the changed-file diffs are already in hand on the fetched pull request, so
    // no additional provider call is made. When retention is off it performs no diff-building work, and
    // when the archive consumer is absent it is a no-op.
    private async Task RetainIncrementDiffsAsync(ReviewJob job, PullRequest pr, CancellationToken ct)
    {
        if (reviewArchiveIngestionService is null || scmConnectionRepository is null)
        {
            return;
        }

        try
        {
            var connection = await this.ResolveRetentionConnectionAsync(job, ct);
            if (connection is null || !connection.StoreDiffs)
            {
                return;
            }

            var revisionKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);

            var fileDiffs = pr.ChangedFiles
                .Select(changedFile => new ReviewIncrementFileDiff(
                    changedFile.Path,
                    MapRetainedChangeType(changedFile.ChangeType),
                    changedFile.IsBinary,
                    changedFile.IsBinary ? string.Empty : changedFile.UnifiedDiff))
                .ToList();

            var evt = new ReviewIncrementCompletedEvent(
                job.ClientId,
                connection.Id,
                job.RepositoryId,
                job.PullRequestId,
                revisionKey,
                pr.Status.ToString(),
                DateTimeOffset.UtcNow,
                fileDiffs);

            await reviewArchiveIngestionService.HandleReviewIncrementDiffsAsync(evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Diff retention ingestion failed for PR {PullRequestId}; continuing without archiving.",
                job.PullRequestId);
        }
    }

    // Passive provenance observer: when the producing connection opted in to thread retention, persist a
    // mapping from each provider comment this posting pass created back to the originating job, so a later
    // ingestion step can stamp the job onto retained comments. This is strictly best-effort — it is wrapped
    // so that nothing it does can disrupt or change publishing. When retention is off, the store is absent,
    // or no provider comment ids were captured, it records nothing.
    private async Task RecordPostedCommentOriginsAsync(
        ReviewJob job,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null || diagnostics.PostedComments.Count == 0)
        {
            return;
        }

        try
        {
            var connection = await this.ResolveRetentionConnectionAsync(job, ct);
            if (connection is null || !connection.StoreThreads)
            {
                return;
            }

            var postedAt = DateTimeOffset.UtcNow;
            var entries = diagnostics.PostedComments
                .Where(comment => !string.IsNullOrWhiteSpace(comment.ProviderCommentId))
                .Select(comment => new PostedCommentOriginEntry(
                    job.ClientId,
                    job.RepositoryId,
                    job.PullRequestId,
                    comment.ProviderThreadId,
                    comment.ProviderCommentId,
                    job.Id,
                    postedAt))
                .ToList();

            if (entries.Count == 0)
            {
                return;
            }

            await postedCommentOriginStore.RecordAsync(entries, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogPostedCommentOriginRecordingFailed(logger, job.Id, ex);
        }
    }

    private async Task<ClientScmConnectionDto?> ResolveRetentionConnectionAsync(ReviewJob job, CancellationToken ct)
    {
        if (scmConnectionRepository is null)
        {
            return null;
        }

        var host = job.ProviderHost;
        var connections = await scmConnectionRepository.GetByClientIdAsync(job.ClientId, ct);

        return connections
            .Where(connection => connection.IsActive
                                 && connection.ProviderFamily == host.Provider
                                 && ConnectionHostMatchesAuthority(connection.HostBaseUrl, host.HostBaseUrl))
            // Prefer the most specific host match when several connections share an authority.
            .OrderByDescending(connection => connection.HostBaseUrl.Length)
            .FirstOrDefault();
    }

    private static bool ConnectionHostMatchesAuthority(string connectionHostBaseUrl, string hostAuthority)
    {
        // The job host is normalized to an authority (scheme://host[:port]); a connection's stored host
        // base URL may carry a path (e.g. an Azure DevOps organization URL). Match on the authority.
        if (!Uri.TryCreate(connectionHostBaseUrl.Trim(), UriKind.Absolute, out var connectionUri))
        {
            return string.Equals(
                connectionHostBaseUrl.Trim().TrimEnd('/'),
                hostAuthority.Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        var connectionAuthority = connectionUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return string.Equals(connectionAuthority, hostAuthority.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string MapRetainedChangeType(ChangeType changeType)
    {
        return changeType switch
        {
            ChangeType.Add => "Added",
            ChangeType.Edit => "Modified",
            ChangeType.Delete => "Deleted",
            ChangeType.Rename => "Renamed",
            _ => "Unknown",
        };
    }

    private async Task SaveScanAndDeleteJobAsync(ReviewJob job, PullRequest pr, Guid? reviewerId, CancellationToken ct)
    {
        await this.SaveScanAsync(
            job,
            GetReviewerThreads(pr, reviewerId),
            reviewerId,
            pr.AuthorizedIdentityName,
            pr.AuthorizedIdentityId,
            ct);
        await jobs.DeleteAsync(job.Id, ct);
    }

    private async Task PublishReviewResultAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewResult result,
        int? compareToIterationId,
        CancellationToken ct)
    {
        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(job.Id, job.RetryCount + 1, "posting", ct: ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, job.Id, ex);
        }

        try
        {
            var publicationResult = this.PrepareResultForPublication(job, pr, result);
            var scmCommentPostingEnabled = await clientRegistry.GetScmCommentPostingEnabledAsync(job.ClientId, ct);
            var publicationIdentity = ResolvePublicationIdentity(job, pr);
            var diagnostics = ReviewCommentPostingDiagnosticsDto.Empty(
                publicationResult.Comments.Count + publicationResult.CarriedForwardCandidatesSkipped,
                publicationResult.CarriedForwardCandidatesSkipped);

            if (scmCommentPostingEnabled)
            {
                var publicationRevision = await this.ResolvePublicationReviewRevisionAsync(job, ct);
                var publicationContext = BuildPublicationContext(
                    job,
                    pr,
                    publicationRevision,
                    publicationIdentity,
                    compareToIterationId);
                diagnostics = await providerRegistry.GetCodeReviewPublicationService(job.Provider)
                    .PublishReviewAsync(
                        job.ClientId,
                        job.CodeReviewReference,
                        publicationRevision,
                        publicationResult,
                        publicationIdentity,
                        ct,
                        publicationContext);
            }

            await jobs.SetResultAsync(job.Id, publicationResult, ct);

            await this.RecordPostedCommentOriginsAsync(job, diagnostics, ct);

            if (protocolId.HasValue)
            {
                await this.RecordPostingDiagnosticsAsync(protocolId.Value, diagnostics, ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Completed", 0, 0, 0, 0, null, ct);
            }

            LogReviewCompleted(logger, job.Id);
        }
        catch (Exception ex)
        {
            if (protocolId.HasValue)
            {
                // A total publication failure still carries the per-thread provider errors — record them so the
                // diagnostics are not lost when nothing posted.
                if (ex is ReviewCommentPublicationFailedException publicationFailure)
                {
                    await this.RecordPostingDiagnosticsAsync(protocolId.Value, publicationFailure.Diagnostics, ct);
                }

                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value,
                    "memory_operation_failed",
                    JsonSerializer.Serialize(
                        new
                        {
                            operation = "publish_review_result",
                            jobId = job.Id,
                            pullRequestId = job.PullRequestId,
                            iterationId = job.IterationId,
                            repositoryId = job.RepositoryId,
                            clientId = job.ClientId,
                            errorType = ex.GetType().FullName,
                            errorMessage = ex.Message,
                        }),
                    $"Failed while posting the review result: {ex.Message}",
                    ct);
                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }

            throw;
        }
    }

    // Resolve the optional configured trigger reviewer plus any effective reviewer-owned identity key.
    private async Task<ResolvedReviewerContext> ResolveReviewerAsync(
        ReviewJob job,
        CancellationToken ct)
    {
        var configuredTriggerReviewer = await clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, ct);
        var effectiveReviewer = configuredTriggerReviewer
                                ?? await clientRegistry.GetEffectiveReviewerIdentityAsync(job.ClientId, job.ProviderHost, ct);
        Guid? effectiveReviewerId = null;
        if (effectiveReviewer is not null)
        {
            effectiveReviewerId = Guid.TryParse(effectiveReviewer.ExternalUserId, out var parsedReviewerId)
                ? parsedReviewerId
                : StableGuidGenerator.Create(effectiveReviewer.ExternalUserId);
        }

        return new ResolvedReviewerContext(configuredTriggerReviewer, effectiveReviewerId);
    }

    // T070: Resolve per-client AI connection — returns null when not configured (caller sets job failed).
    private async Task<(IChatClient ChatClient, AgentReviewRuntimeCapabilities Capabilities)?> ResolveAiConnectionAsync(ReviewJob job, CancellationToken ct)
    {
        if (aiRuntimeResolver is not null)
        {
            try
            {
                var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ReviewDefault, ct);
                job.SetAiConfig(runtime.Connection.Id, runtime.Model.RemoteModelId, job.ReviewTemperature);
                await jobs.UpdateAiConfigAsync(job.Id, runtime.Connection.Id, runtime.Model.RemoteModelId, ct, job.ReviewTemperature);
                return (runtime.ChatClient, runtime.Capabilities);
            }
            catch (Exception ex)
            {
                LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
                await jobs.SetFailedAsync(job.Id, ex.Message, ct);
                return null;
            }
        }

        var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(job.ClientId, ct);
        if (activeConnection is null)
        {
            LogNoAiConnectionConfigured(logger, job.ClientId, job.Id);
            await jobs.SetFailedAsync(
                job.Id,
                $"No active AI connection configured for client {job.ClientId}. Configure one via the admin UI.",
                ct);
            return null;
        }

        var effectiveModelId = activeConnection.GetBoundModelId(AiPurpose.ReviewDefault)
                               ?? activeConnection.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            await jobs.SetFailedAsync(
                job.Id,
                $"Active AI connection for client {job.ClientId} has no model deployment selected. Activate a deployment in the admin UI.",
                ct);
            return null;
        }

        var client = aiChatClientFactory.CreateClient(activeConnection.BaseUrl, activeConnection.Secret);
        job.SetAiConfig(activeConnection.Id, effectiveModelId, job.ReviewTemperature);
        await jobs.UpdateAiConfigAsync(job.Id, activeConnection.Id, effectiveModelId, ct, job.ReviewTemperature);
        return (client, new AgentReviewRuntimeCapabilities(false, false, false, false));
    }

    // Load scan state — returns the scan, whether a new revision exists, the reusable carry-forward baseline
    // (with whether it covered its full revision), any same-revision resume job, and the provider-neutral
    // delta-compare handle when the baseline is full-coverage.
    private async Task<(
        ReviewPrScan? scan,
        bool isNewIteration,
        ReviewJob? baselineJob,
        bool baselineIsFullCoverage,
        ReviewJob? resumeJob,
        int? compareToIterationId,
        ReviewRevision? compareToReviewRevision)> LoadScanStateAsync(
        ReviewJob job,
        CancellationToken ct)
    {
        var scan = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
        var iterationKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
        var isNewIteration = scan is null || scan.LastProcessedCommitId != iterationKey;

        ReviewJob? baselineJob = null;
        var baselineIsFullCoverage = false;
        ReviewJob? resumeJob = null;
        int? compareToIterationId = null;
        ReviewRevision? compareToReviewRevision = null;
        var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(job.ReviewRevisionReference);

        if (!string.IsNullOrWhiteSpace(currentRevisionKey))
        {
            resumeJob = await jobs.GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                currentRevisionKey,
                ct);

            if (resumeJob?.Id == job.Id)
            {
                resumeJob = null;
            }
        }

        if (isNewIteration)
        {
            // Select the carry-forward baseline from job history — the most-recent terminal job at a
            // different revision — rather than from the scan. This lets a prior review that was
            // cancelled/failed/superseded mid-flight still seed the next review's unchanged files.
            baselineJob = await jobs.GetLatestReusableTerminalJobAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.Id,
                iterationKey,
                ct);

            if (baselineJob is not null)
            {
                baselineIsFullCoverage = ReviewBaselineSelection.IsFullCoverage(baselineJob);
                if (baselineIsFullCoverage)
                {
                    // Full-coverage baseline: delta-scope against it so only files changed since the
                    // baseline are re-reviewed. The compare handle is provider-neutral — Azure DevOps
                    // reads the iteration id off the baseline job, other providers read its review revision.
                    if (job.Provider == ScmProvider.AzureDevOps)
                    {
                        var baselineIterationId = ResolveBaselineIterationId(baselineJob);
                        if (baselineIterationId is > 0 && baselineIterationId < job.IterationId)
                        {
                            compareToIterationId = baselineIterationId;
                        }
                        else
                        {
                            // Out-of-order or unavailable iteration id: fall back to a full fetch and treat
                            // the baseline purely as an AI-skip set rather than risk a negative delta.
                            baselineIsFullCoverage = false;
                        }
                    }
                    else
                    {
                        compareToReviewRevision = baselineJob.ReviewRevisionReference;
                    }
                }
            }
        }

        return (scan, isNewIteration, baselineJob, baselineIsFullCoverage, resumeJob, compareToIterationId, compareToReviewRevision);
    }

    // Derives the Azure DevOps iteration id to compare against from the baseline job itself: prefer the
    // iteration id carried in its review revision (ProviderRevisionId), falling back to the stored iteration.
    private static int? ResolveBaselineIterationId(ReviewJob baselineJob)
    {
        var iterationFromRevision = ReviewRevisionKeys.TryParseIterationId(ReviewRevisionKeys.TryGetStoredKey(baselineJob.ReviewRevisionReference));
        return iterationFromRevision ?? (baselineJob.IterationId > 0 ? baselineJob.IterationId : null);
    }

    // T072: Fetch PR and guard the active status — returns null if PR is no longer active (job already updated).
    private async Task<PullRequestRef> FetchPullRequestRefAsync(ReviewJob job, CancellationToken ct)
    {
        return await prFetcher.FetchRefAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.ClientId,
            ct);
    }

    private static async Task DisposeEarlyWorkspaceAsync(
        IReviewRepositoryWorkspace? workspace,
        ReviewRepositoryWorkspacePreparationResult preparation)
    {
        if (workspace is not null && preparation.Succeeded)
        {
            await workspace.DisposeAsync();
        }
    }

    private async Task<ReviewRepositoryWorkspacePreparationResult> PrepareWorkspaceForFetchAsync(
        ReviewJob job,
        PullRequestRef prRef,
        CancellationToken ct)
    {
        if (workspaceManager is null)
        {
            throw new InvalidOperationException("No workspace manager is registered. Local review workspace support is required.");
        }

        return await workspaceManager.PrepareAsync(
            new ReviewRepositoryWorkspaceRequest(
                job.Id,
                job.ClientId,
                job.Provider,
                job.OrganizationUrl,
                job.CodeReviewReference.Repository,
                job.PullRequestId,
                job.ReviewRevisionReference ?? throw new InvalidOperationException("A review revision is required for local workspace preparation."),
                prRef.SourceBranch,
                prRef.TargetBranch),
            ct);
    }

    private async Task<PullRequest?> FetchPullRequestAsync(
        ReviewJob job,
        int? compareToIterationId,
        ReviewRevision? compareToReviewRevision,
        IReviewRepositoryWorkspace? workspace,
        CancellationToken ct)
    {
        var pr = await prFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            compareToIterationId,
            job.ClientId,
            ct,
            compareToReviewRevision,
            workspace);

        if (pr.Status == PrStatus.Active)
        {
            return pr;
        }

        LogPrNoLongerActive(logger, job.PullRequestId, pr.Status, job.Id);
        if (pr.Status == PrStatus.Abandoned)
        {
            LogPrAbandonedCancellingJob(logger, job.PullRequestId, job.Id);
            await jobs.SetCancelledAsync(job.Id, ct);
        }
        else
        {
            await jobs.SetFailedAsync(job.Id, "PR was closed or abandoned before review could begin", ct);
        }

        return null;
    }

    // T073: Evaluate existing reviewer threads if any are present.
    private async Task EvaluateExistingThreadsAsync(
        ReviewJob job,
        PullRequest pr,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan? scan,
        bool isNewIteration,
        Guid? reviewerId,
        IReadOnlyList<string> providerCapabilities,
        IChatClient chatClient,
        CancellationToken ct)
    {
        if (reviewerThreads.Count == 0)
        {
            return;
        }

        if (!providerCapabilities.Any(capability => string.Equals(
                capability,
                "reviewThreadStatus",
                StringComparison.Ordinal)))
        {
            return;
        }

        var behavior = await clientRegistry.GetCommentResolutionBehaviorAsync(job.ClientId, ct);
        var canReply = providerCapabilities.Any(capability => string.Equals(
            capability,
            "reviewThreadReply",
            StringComparison.Ordinal));
        await this.EvaluateReviewerThreadsAsync(
            job,
            pr,
            reviewerThreads,
            scan,
            isNewIteration,
            behavior,
            reviewerId,
            canReply,
            chatClient,
            ct);
    }

    // Build review context — reuse prior results, fetch instructions and exclusions.
    // Returns (systemContext, carriedForwardPaths); systemContext is null when all files were carried
    // forward with an empty delta (no AI review needed — caller should save scan and delete job).
    private async Task<(ReviewSystemContext? systemContext, List<string> carriedForwardPaths)> BuildReviewContextAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewJob? baselineJob,
        bool baselineIsFullCoverage,
        ReviewJob? resumeJob,
        IChatClient chatClient,
        AgentReviewRuntimeCapabilities runtimeCapabilities,
        ReviewRepositoryWorkspacePreparationResult preparedWorkspace,
        CancellationToken ct)
    {
        var changedFilePaths = pr.ChangedFiles.Select(f => f.Path).ToList();
        var changedPathsSet = new HashSet<string>(changedFilePaths, StringComparer.OrdinalIgnoreCase);

        // Fetch exclusion rules up front: on the partial-baseline (full-fetch) path a baseline-reviewed
        // file that now matches an exclusion rule must be excluded rather than carried forward stale.
        var exclusionRules = await this.FetchExclusionRulesAsync(job, pr, ct);

        // Same-revision resume (files changed at this revision) and cross-revision carry-forward (unchanged
        // files) must never both write a result row for the same path. Resume runs first so a result computed
        // at the current revision wins over an inherited one from an earlier revision.
        // Case-insensitive to match changedPathsSet so the no-duplicate guarantee holds even when resume and
        // carry-forward emit the same logical path in different casing (e.g. src/File.cs vs src/file.cs).
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await this.ResumePriorFileResultsAsync(job, resumeJob, changedPathsSet, claimedPaths, ct);
        var carriedForwardPaths = await this.CarryForwardBaselineResultsAsync(
            job, baselineJob, baselineIsFullCoverage, changedPathsSet, exclusionRules, claimedPaths, ct);

        if (changedFilePaths.Count == 0 && (carriedForwardPaths.Count > 0 || resumeJob is not null))
        {
            return (null, carriedForwardPaths);
        }

        var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);
        var enableEvidenceBackedVerification = await clientRegistry.GetEvidenceBackedVerificationEnabledAsync(job.ClientId, ct);
        var enableLanguageRobustScreening = await clientRegistry.GetLanguageRobustScreeningEnabledAsync(job.ClientId, ct);
        var enableMultiPassUnion = await clientRegistry.GetMultiPassUnionEnabledAsync(job.ClientId, ct);
        var includeLinkedItemsInContext = await clientRegistry.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, ct);
        var reviewPasses = await clientRegistry.GetReviewPassesAsync(job.ClientId, ct);
        var baselineReasoningEffort = await clientRegistry.GetBaselineReasoningEffortAsync(job.ClientId, ct);

        var workspacePreparation = preparedWorkspace;

        if (!workspacePreparation.Succeeded)
        {
            var failure = workspacePreparation.Failure;
            throw new InvalidOperationException($"Local review workspace preparation failed at stage '{failure?.Stage}' ({failure?.Code}): {failure?.Message}");
        }

        await this.RecordWorkspaceProtocolAsync(job, workspacePreparation, ct);

        var reviewTools = reviewContextToolsFactory.Create(
            new ReviewContextToolsRequest(
                job.CodeReviewReference,
                pr.SourceBranch,
                job.IterationId,
                job.ClientId,
                job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                    ? job.ProCursorSourceIds
                    : null,
                job.OrganizationUrl,
                pr.TargetBranch,
                pr.ChangedFiles.Select(ChangedPathSnapshot.FromChangedFile).ToList().AsReadOnly(),
                Workspace: workspacePreparation.Workspace,
                WorkspaceLease: workspacePreparation.Workspace?.Lease,
                WorkspaceFailure: workspacePreparation.Failure));
        var fetchedInstructions = await instructionFetcher.FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            pr.TargetBranch,
            job.ClientId,
            ct);
        var relevantInstructions = fetchedInstructions.Count > 0
            ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, changedFilePaths, ct)
            : [];

        var systemContext = new ReviewSystemContext(customSystemMessage, relevantInstructions, reviewTools)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = job.AiModel,
            RuntimeCapabilities = runtimeCapabilities,
            EnableEvidenceBackedVerification = enableEvidenceBackedVerification,
            EnableLanguageRobustScreening = enableLanguageRobustScreening,
            EnableMultiPassUnion = enableMultiPassUnion,
            IncludeLinkedItemsInContext = includeLinkedItemsInContext,
            ReviewPasses = reviewPasses,
            BaselineReasoningEffort = baselineReasoningEffort,
            ExclusionRules = exclusionRules,
            ModelId = job.AiModel,
            ProtocolRecorder = protocolRecorder,
            Temperature = job.ReviewTemperature,
            PromptOverrides = await LoadPromptOverridesAsync(job.ClientId, promptOverrideService, logger, ct),
            ReviewWorkspace = workspacePreparation.Workspace,
        };

        return (systemContext, carriedForwardPaths);
    }

    // Discovers the work items / issues linked to the pull request (when the client opted in) and attaches a
    // bounded, deduplicated summary to the PullRequest so it renders into the review prompt. Fail-soft: any
    // discovery error leaves the review to proceed without linked-item context. Never logs item titles/bodies.
    private async Task<PullRequest> AttachLinkedItemsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext systemContext,
        CancellationToken ct)
    {
        if (!systemContext.IncludeLinkedItemsInContext)
        {
            return pr;
        }

        try
        {
            var provider = providerRegistry.GetLinkedItemProvider(job.Provider);
            var discovered = await provider.DiscoverLinkedItemsAsync(job.ClientId, pr, ct);
            var bounded = LinkedItemContextBounding.Bound(
                discovered,
                this._opts.MaxLinkedItemsInContext,
                this._opts.MaxLinkedItemDescriptionChars,
                out var droppedCount);

            if (bounded.Count == 0)
            {
                return pr;
            }

            LogLinkedItemsAttached(logger, job.Id, discovered.Count, bounded.Count, droppedCount);
            return pr with { LinkedItems = bounded };
        }
        catch (Exception ex)
        {
            LogLinkedItemsSkipped(logger, job.Id, ex);
            return pr;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Attached linked items to the review context for job {JobId}: {DiscoveredCount} discovered, {InjectedCount} injected, {DroppedCount} dropped by cap.")]
    private static partial void LogLinkedItemsAttached(
        ILogger logger,
        Guid jobId,
        int discoveredCount,
        int injectedCount,
        int droppedCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Linked-item discovery unavailable for job {JobId}; proceeding without linked-item context.")]
    private static partial void LogLinkedItemsSkipped(ILogger logger, Guid jobId, Exception ex);

    // Carries forward reviewed file results from a prior baseline job at a different revision.
    //
    // Full-coverage baseline: the current fetch is delta-scoped against it, so a reviewed file that is NOT
    // in the delta (<paramref name="changedPathsSet" /> holds the delta) is provably unchanged and carries
    // forward. This is the long-standing behaviour and is unchanged for a completed baseline.
    //
    // Partial baseline (cancelled/failed/superseded mid per-file review): the current fetch is the full PR
    // (<paramref name="changedPathsSet" /> holds every current file), so carry forward every reviewed file
    // still present as an AI-skip set and let the dispatcher review the rest fresh — this keeps files that
    // are unchanged since the baseline but were never reviewed by it from being silently skipped.
    private async Task<List<string>> CarryForwardBaselineResultsAsync(
        ReviewJob job,
        ReviewJob? baselineJob,
        bool baselineIsFullCoverage,
        HashSet<string> changedPathsSet,
        ReviewExclusionRules exclusionRules,
        HashSet<string> claimedPaths,
        CancellationToken ct)
    {
        var carriedForwardPaths = new List<string>();
        if (baselineJob is null)
        {
            return carriedForwardPaths;
        }

        foreach (var priorResult in baselineJob.FileReviewResults
                     .Where(fr => fr.IsComplete && !fr.IsFailed && !fr.IsExcluded && !fr.IsCarriedForward))
        {
            var shouldCarryForward = baselineIsFullCoverage
                ? !changedPathsSet.Contains(priorResult.FilePath)
                : changedPathsSet.Contains(priorResult.FilePath);
            if (!shouldCarryForward)
            {
                continue;
            }

            // Exclusion drift only applies on the partial (full-fetch) path: there the skipped file still
            // reaches the dispatcher to be recorded as excluded. On the delta path the file is absent from
            // the fetch, so skipping carry-forward would drop it entirely — preserve carry-forward there.
            if (!baselineIsFullCoverage && exclusionRules.Matches(priorResult.FilePath))
            {
                continue;
            }

            if (!claimedPaths.Add(priorResult.FilePath))
            {
                continue;
            }

            var carried = ReviewFileResult.CreateCarriedForward(job.Id, priorResult);
            await jobs.AddFileResultAsync(carried, ct);
            carriedForwardPaths.Add(priorResult.FilePath);
        }

        return carriedForwardPaths;
    }

    // Resumes file results from a prior job targeting the same review revision for files that
    // are still part of the current change set, so completed work is not redone.
    private async Task ResumePriorFileResultsAsync(
        ReviewJob job,
        ReviewJob? resumeJob,
        HashSet<string> changedPathsSet,
        HashSet<string> claimedPaths,
        CancellationToken ct)
    {
        if (resumeJob is null)
        {
            return;
        }

        foreach (var priorResult in resumeJob.FileReviewResults
                     .Where(fr => fr.IsComplete && !fr.IsFailed && !fr.IsExcluded && !fr.IsCarriedForward))
        {
            if (!changedPathsSet.Contains(priorResult.FilePath))
            {
                continue;
            }

            if (!claimedPaths.Add(priorResult.FilePath))
            {
                continue;
            }

            var resumed = ReviewFileResult.CreateResumed(job.Id, priorResult);
            await jobs.AddFileResultAsync(resumed, ct);
        }
    }

    // Fetches the repository exclusion rules for the review target branch. IRepositoryExclusionFetcher is
    // contractually non-throwing and returns defaults on failure; the catch is belt-and-suspenders.
    private async Task<ReviewExclusionRules> FetchExclusionRulesAsync(ReviewJob job, PullRequest pr, CancellationToken ct)
    {
        try
        {
            return await exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pr.TargetBranch,
                job.ClientId,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch exclusion rules for job {JobId}; using defaults", job.Id);
            return ReviewExclusionRules.Default;
        }
    }

    private async Task RecordWorkspaceProtocolAsync(
        ReviewJob job,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        CancellationToken ct)
    {
        if (!workspacePreparation.Succeeded && workspacePreparation.Failure is null)
        {
            return;
        }

        if (protocolRecorder is null)
        {
            return;
        }

        var protocolId = jobs.GetById(job.Id)?.Protocols
            .OrderByDescending(protocol => protocol.StartedAt)
            .FirstOrDefault()?.Id;
        if (!protocolId.HasValue)
        {
            return;
        }

        if (workspacePreparation.Workspace is not null)
        {
            var details = JsonSerializer.Serialize(
                new
                {
                    attempted = true,
                    prepared = true,
                    fallbackApplied = false,
                    workspaceKey = workspacePreparation.Workspace.Lease.WorkspaceKey,
                });
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                LocalWorkspacePreparedEventName,
                details,
                details,
                null,
                ct);
            return;
        }

        if (workspacePreparation.Failure is not null)
        {
            var failureDetails = JsonSerializer.Serialize(
                new
                {
                    attempted = true,
                    prepared = false,
                    fallbackApplied = true,
                    stage = workspacePreparation.Failure.Stage,
                    code = workspacePreparation.Failure.Code,
                    message = workspacePreparation.Failure.Message,
                });
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                LocalWorkspaceFailedEventName,
                failureDetails,
                failureDetails,
                null,
                ct);
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                LocalWorkspaceFallbackAppliedEventName,
                failureDetails,
                failureDetails,
                null,
                ct);
        }
    }

    // T075: Dispatch the file-by-file review and merge carry-forward paths into the result.
    private async Task<ReviewResult> DispatchFileReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext systemContext,
        IChatClient chatClient,
        CancellationToken ct)
    {
        try
        {
            return await fileByFileReviewOrchestrator.ReviewAsync(job, pr, systemContext, ct, chatClient);
        }
        finally
        {
            if (systemContext.ReviewWorkspace is not null)
            {
                await systemContext.ReviewWorkspace.DisposeAsync();
                systemContext.ReviewWorkspace = null;
            }
        }
    }

    private async Task HandlePartialReviewFailureAsync(
        ReviewJob job,
        PullRequest? pr,
        PartialReviewFailureException ex,
        CancellationToken ct)
    {
        LogPartialReviewFailure(logger, job.Id, ex.FailedCount, ex.TotalCount);

        job.RetryCount++;
        await jobs.UpdateRetryCountAsync(job.Id, job.RetryCount, ct);

        if (job.RetryCount >= this._opts.MaxFileReviewRetries)
        {
            // On the final retry, post any partial results from the files that succeeded
            // rather than silently discarding them.
            if (ex.PartialResult is { } partial &&
                (!string.IsNullOrWhiteSpace(partial.Summary) || partial.Comments.Count > 0))
            {
                var reviewerContext = await this.ResolveReviewerAsync(job, ct);
                try
                {
                    await this.PublishReviewResultAsync(
                        job,
                        pr ?? new PullRequest(
                            job.OrganizationUrl,
                            job.ProjectId,
                            job.RepositoryId,
                            job.RepositoryId,
                            job.PullRequestId,
                            job.IterationId,
                            string.Empty,
                            null,
                            string.Empty,
                            string.Empty,
                            [],
                            ExistingThreads: pr?.ExistingThreads),
                        partial,
                        null,
                        ct);
                    return;
                }
                catch (Exception postEx)
                {
                    LogReviewFailed(logger, job.Id, postEx);
                }
            }

            await jobs.SetFailedAsync(job.Id, $"Max retries reached. {ex.Message}", ct);
        }
        else
        {
            // Re-queue the job so the worker picks it up again without waiting for a restart.
            // FileByFileReviewOrchestrator skips already-completed file results on the next pass.
            await jobs.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending, ct);
        }
    }

    private async Task RecordPostingDiagnosticsAsync(
        Guid protocolId,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        var summaryDetails = JsonSerializer.Serialize(
            new
            {
                candidateCount = diagnostics.CandidateCount,
                postedCount = diagnostics.PostedCount,
                suppressedCount = diagnostics.SuppressedCount,
                failedCount = diagnostics.FailedCount,
                suppressionReasons = diagnostics.SuppressionReasons,
                consideredOpenThreads = diagnostics.ConsideredOpenThreads,
                consideredResolvedThreads = diagnostics.ConsideredResolvedThreads,
                usedFallbackChecks = diagnostics.UsedFallbackChecks,
                carriedForwardCandidatesSkipped = diagnostics.CarriedForwardCandidatesSkipped,
            });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_summary", summaryDetails, null, ct);

        await this.RecordPostingFailuresAsync(protocolId, diagnostics, ct);

        if (!diagnostics.IsDegraded)
        {
            return;
        }

        var degradedModeDetails = JsonSerializer.Serialize(
            new
            {
                cause = diagnostics.DegradedCause ?? "Duplicate protection ran in degraded mode.",
                degradedComponents = diagnostics.DegradedComponents,
                fallbackChecks = diagnostics.FallbackChecks,
                affectedCandidateCount = diagnostics.AffectedCandidateCount,
                reviewContinued = true,
            });

        await protocolRecorder.RecordDedupEventAsync(protocolId, "dedup_degraded_mode", degradedModeDetails, null, ct);
    }

    private async Task RecordPostingFailuresAsync(
        Guid protocolId,
        ReviewCommentPostingDiagnosticsDto diagnostics,
        CancellationToken ct)
    {
        foreach (var failure in diagnostics.PostingFailures)
        {
            var failureDetails = JsonSerializer.Serialize(
                new
                {
                    threadKind = failure.ThreadKind,
                    filePath = failure.FilePath,
                    line = failure.Line,
                });

            await protocolRecorder.RecordPublicationEventAsync(
                protocolId,
                "publication_thread_post_failed",
                failureDetails,
                failure.Error,
                ct);
        }
    }

    private ReviewResult PrepareResultForPublication(ReviewJob job, PullRequest pr, ReviewResult result)
    {
        if (!RequiresInsertedInlineAnchors(job.Provider) || result.Comments.Count == 0)
        {
            return result;
        }

        var insertedLinesByPath = BuildInsertedLineLookup(pr.ChangedFiles);
        var normalizedComments = new List<ReviewComment>(result.Comments.Count);
        var downgradedCount = 0;

        foreach (var comment in result.Comments)
        {
            if (!CanUseGitLabInlineAnchor(comment, insertedLinesByPath) &&
                !string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber.HasValue &&
                comment.LineNumber.Value > 0)
            {
                downgradedCount++;
                normalizedComments.Add(
                    new ReviewComment(
                        null,
                        null,
                        comment.Severity,
                        $"{NormalizeReviewPath(comment.FilePath)}:L{comment.LineNumber.Value}: {comment.Message}"));
                continue;
            }

            normalizedComments.Add(comment);
        }

        if (downgradedCount == 0)
        {
            return result;
        }

        logger.LogInformation(
            "Downgraded {DowngradedCount} {Provider} inline review comment(s) to overview comments for job {JobId} because the referenced lines were not inserted diff lines.",
            downgradedCount,
            job.Provider,
            job.Id);

        return result with { Comments = normalizedComments.AsReadOnly() };
    }

    private static bool RequiresInsertedInlineAnchors(ScmProvider provider)
    {
        return provider is ScmProvider.GitLab or ScmProvider.Forgejo;
    }

    private static bool CanUseGitLabInlineAnchor(
        ReviewComment comment,
        IReadOnlyDictionary<string, HashSet<int>> insertedLinesByPath)
    {
        if (string.IsNullOrWhiteSpace(comment.FilePath) || !comment.LineNumber.HasValue || comment.LineNumber.Value < 1)
        {
            return true;
        }

        return insertedLinesByPath.TryGetValue(NormalizeReviewPath(comment.FilePath), out var insertedLines)
               && insertedLines.Contains(comment.LineNumber.Value);
    }

    private static IReadOnlyDictionary<string, HashSet<int>> BuildInsertedLineLookup(IReadOnlyList<ChangedFile> changedFiles)
    {
        var lookup = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var changedFile in changedFiles)
        {
            if (changedFile.IsBinary)
            {
                continue;
            }

            lookup[NormalizeReviewPath(changedFile.Path)] = ExtractInsertedNewLines(changedFile);
        }

        return lookup;
    }

    private static HashSet<int> ExtractInsertedNewLines(ChangedFile changedFile)
    {
        var insertedLines = new HashSet<int>();
        var diffLines = changedFile.UnifiedDiff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasHunkHeader = false;
        var currentNewLine = 0;

        foreach (var diffLine in diffLines)
        {
            ProcessUnifiedDiffLine(diffLine, insertedLines, ref currentNewLine, ref hasHunkHeader);
        }

        if (!hasHunkHeader && changedFile.ChangeType == ChangeType.Add)
        {
            var lineCount = CountLines(changedFile.FullContent);
            for (var lineNumber = 1; lineNumber <= lineCount; lineNumber++)
            {
                insertedLines.Add(lineNumber);
            }
        }

        return insertedLines;
    }

    // Classifies a single unified-diff line and updates the running new-file line cursor.
    private static void ProcessUnifiedDiffLine(
        string diffLine,
        HashSet<int> insertedLines,
        ref int currentNewLine,
        ref bool hasHunkHeader)
    {
        if (diffLine.StartsWith("@@", StringComparison.Ordinal))
        {
            if (TryParseUnifiedDiffNewLineStart(diffLine, out var newLineStart))
            {
                currentNewLine = newLineStart;
                hasHunkHeader = true;
            }

            return;
        }

        if (!hasHunkHeader)
        {
            return;
        }

        switch (ReviewDiffProcessor.ClassifyHunkLine(diffLine))
        {
            case HunkLineKind.Added:
                insertedLines.Add(currentNewLine);
                currentNewLine++;
                break;
            case HunkLineKind.Context:
                currentNewLine++;
                break;
            case HunkLineKind.Removed:
            case HunkLineKind.Marker:
                // Removed lines and non-payload markers occupy no new-file line.
                break;
        }
    }

    private static bool TryParseUnifiedDiffNewLineStart(string diffLine, out int newLineStart)
    {
        newLineStart = 0;

        var plusIndex = diffLine.IndexOf('+');
        if (plusIndex < 0)
        {
            return false;
        }

        var endIndex = plusIndex + 1;
        while (endIndex < diffLine.Length && char.IsDigit(diffLine[endIndex]))
        {
            endIndex++;
        }

        return endIndex > plusIndex + 1
               && int.TryParse(diffLine[(plusIndex + 1)..endIndex], out newLineStart)
               && newLineStart > 0;
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
    }

    private static string NormalizeReviewPath(string path)
    {
        return path.TrimStart('/');
    }

    private static IReadOnlyList<PrCommentThread> GetReviewerThreads(PullRequest pr, Guid? reviewerId)
    {
        if (pr.ExistingThreads is null)
        {
            return [];
        }

        return pr.ExistingThreads
            .Where(t => IsReviewerOwnedAuthor(
                t.Comments.FirstOrDefault()?.AuthorId,
                t.Comments.FirstOrDefault()?.AuthorName,
                reviewerId,
                pr.AuthorizedIdentityId,
                pr.AuthorizedIdentityName))
            .ToList()
            .AsReadOnly();
    }

    private static bool HasNewThreadReplies(
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan scan,
        Guid? reviewerId,
        Guid? authorizedIdentityId,
        string? authorizedIdentityName)
    {
        foreach (var thread in reviewerThreads)
        {
            var stored = scan.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);
            var storedCount = stored?.LastSeenReplyCount ?? 0;
            var userReplyCount = CountNonReviewerComments(
                thread.Comments,
                reviewerId,
                authorizedIdentityId,
                authorizedIdentityName);
            if (userReplyCount > storedCount)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Loads prompt overrides for every known prompt key for the given client.
    ///     Returns an empty dictionary on null service, cancellation, or any exception (graceful degradation).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> LoadPromptOverridesAsync(
        Guid clientId,
        IPromptOverrideService? service,
        ILogger logger,
        CancellationToken ct)
    {
        if (service is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in PromptOverride.ValidPromptKeys)
            {
                var text = await service.GetOverrideAsync(clientId, null, key, ct);
                if (text is not null)
                {
                    overrides[key] = text;
                }
            }

            return overrides;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to load prompt overrides for client {ClientId}; review will proceed with global defaults",
                clientId);
            return new Dictionary<string, string>();
        }
    }

    private async Task EvaluateReviewerThreadsAsync(
        ReviewJob job,
        PullRequest pr,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        ReviewPrScan? scan,
        bool isNewIteration,
        CommentResolutionBehavior behavior,
        Guid? reviewerId,
        bool canReply,
        IChatClient chatClient,
        CancellationToken ct)
    {
        if (behavior == CommentResolutionBehavior.Disabled)
        {
            return;
        }

        foreach (var thread in reviewerThreads)
        {
            var stored = scan?.Threads.FirstOrDefault(t => t.ThreadId == thread.ThreadId);

            // Skip threads that ADO already reports as resolved — no AI call needed.
            if (IsResolvedStatus(thread.Status))
            {
                continue;
            }

            try
            {
                ThreadResolutionResult resolution;

                var storedCount = stored?.LastSeenReplyCount ?? 0;
                var userReplyCount = CountNonReviewerComments(
                    thread.Comments,
                    reviewerId,
                    pr.AuthorizedIdentityId,
                    pr.AuthorizedIdentityName);
                var hasNewReplies = userReplyCount > storedCount;

                var evaluationKind = isNewIteration ? "code-change" : "conversational";
                Guid? protocolId = null;
                try
                {
                    protocolId = await protocolRecorder.BeginAsync(
                        job.Id,
                        job.RetryCount + 1,
                        $"thread-{thread.ThreadId}-{evaluationKind}",
                        ct: ct);
                }
                catch (Exception ex)
                {
                    LogProtocolBeginFailed(logger, job.Id, ex);
                }

                if (isNewIteration)
                {
                    resolution = await resolutionCore.EvaluateCodeChangeAsync(
                        thread,
                        pr,
                        chatClient,
                        job.AiModel ?? this._opts.ModelId,
                        ct);
                }
                else if (hasNewReplies)
                {
                    resolution = await resolutionCore.EvaluateConversationalReplyAsync(
                        thread,
                        chatClient,
                        job.AiModel ?? this._opts.ModelId,
                        ct);
                }
                else
                {
                    if (protocolId.HasValue)
                    {
                        await protocolRecorder.SetCompletedAsync(protocolId.Value, "Skipped", 0, 0, 0, 0, null, ct);
                    }

                    continue;
                }

                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordAiCallAsync(
                        protocolId.Value,
                        1,
                        resolution.InputTokens,
                        resolution.OutputTokens,
                        null,
                        null,
                        resolution.ReplyText,
                        ct,
                        cachedInputTokens: resolution.CachedInputTokens,
                        cacheWriteTokens: resolution.CacheWriteTokens,
                        reasoningTokens: resolution.ReasoningTokens);
                    var outcome = resolution.IsResolved ? "Resolved" : "NotResolved";
                    await protocolRecorder.SetCompletedAsync(
                        protocolId.Value,
                        outcome,
                        resolution.InputTokens ?? 0,
                        resolution.OutputTokens ?? 0,
                        1,
                        0,
                        null,
                        ct,
                        resolution.CachedInputTokens ?? 0,
                        resolution.CachedInputTokens.HasValue ? CacheObservabilityStatus.Observable : CacheObservabilityStatus.Unobservable,
                        resolution.CacheWriteTokens ?? 0,
                        resolution.ReasoningTokens ?? 0);
                }

                var resolvedAction = BuildResolvedThreadAction(thread, behavior, resolution, canReply);
                if (resolvedAction.ShouldPostReply && resolvedAction.ReplyText is not null)
                {
                    await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                        .ReplyAsync(job.ClientId, CreateReviewThreadRef(job, thread), resolvedAction.ReplyText, ct);
                }

                if (resolvedAction.ShouldResolveThread)
                {
                    await providerRegistry.GetReviewThreadStatusWriter(job.Provider)
                        .UpdateThreadStatusAsync(job.ClientId, CreateReviewThreadRef(job, thread), "fixed", ct);

                    LogThreadResolved(logger, thread.ThreadId, job.PullRequestId);
                }
                else if (canReply && !resolution.IsResolved && resolution.ReplyText is not null && !isNewIteration)
                {
                    await providerRegistry.GetReviewThreadReplyPublisher(job.Provider)
                        .ReplyAsync(job.ClientId, CreateReviewThreadRef(job, thread), resolution.ReplyText, ct);
                }
            }
            catch (Exception ex)
            {
                LogThreadEvaluationFailed(logger, thread.ThreadId, job.PullRequestId, ex);
            }
        }
    }

    private async Task SaveScanAsync(
        ReviewJob job,
        IReadOnlyList<PrCommentThread> reviewerThreads,
        Guid? reviewerId,
        string? authorizedIdentityName,
        Guid? authorizedIdentityId,
        CancellationToken ct)
    {
        try
        {
            var existing = await prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, ct);
            var scanId = existing?.Id ?? Guid.NewGuid();
            var iterationKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);
            var scan = new ReviewPrScan(scanId, job.ClientId, job.RepositoryId, job.PullRequestId, iterationKey);

            foreach (var thread in reviewerThreads)
            {
                scan.Threads.Add(
                    new ReviewPrScanThread
                    {
                        ReviewPrScanId = scanId,
                        ThreadId = thread.ThreadId,
                        LastSeenReplyCount =
                            CountNonReviewerComments(
                                thread.Comments,
                                reviewerId,
                                authorizedIdentityId,
                                authorizedIdentityName),
                        LastSeenStatus = thread.Status,
                    });
            }

            await prScanRepository.UpsertAsync(scan, ct);
        }
        catch (Exception ex)
        {
            LogScanSaveFailed(logger, job.Id, ex);
        }
    }

    private static bool IsResolvedStatus(string? status)
    {
        return string.Equals(status, "Fixed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "WontFix", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "ByDesign", StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedThreadAction BuildResolvedThreadAction(
        PrCommentThread thread,
        CommentResolutionBehavior behavior,
        ThreadResolutionResult resolution,
        bool canReply)
    {
        var normalizedReplyText = string.IsNullOrWhiteSpace(resolution.ReplyText)
            ? null
            : resolution.ReplyText.Trim();

        if (!resolution.IsResolved)
        {
            return new ResolvedThreadAction(
                thread.ThreadId,
                behavior,
                false,
                normalizedReplyText,
                false,
                false,
                ResolvedThreadReasonSource.None);
        }

        if (behavior == CommentResolutionBehavior.WithReply)
        {
            var shouldReplyAndResolve = canReply && normalizedReplyText is not null;
            return new ResolvedThreadAction(
                thread.ThreadId,
                behavior,
                shouldReplyAndResolve,
                normalizedReplyText,
                shouldReplyAndResolve,
                shouldReplyAndResolve,
                shouldReplyAndResolve ? ResolvedThreadReasonSource.AiGenerated : ResolvedThreadReasonSource.None);
        }

        return new ResolvedThreadAction(
            thread.ThreadId,
            behavior,
            true,
            normalizedReplyText,
            false,
            true,
            normalizedReplyText is not null ? ResolvedThreadReasonSource.AiGenerated : ResolvedThreadReasonSource.None);
    }

    private static ReviewerIdentity ResolvePublicationIdentity(ReviewJob job, PullRequest pr)
    {
        var externalUserId = pr.AuthorizedIdentityName
                             ?? pr.AuthorizedIdentityId?.ToString("D")
                             ?? $"connection:{job.ClientId:D}:{job.Provider}:{job.RepositoryId}:{job.PullRequestId}";
        var login = pr.AuthorizedIdentityName ?? externalUserId;
        var displayName = pr.AuthorizedIdentityName ?? login;
        var isBot = job.Provider is ScmProvider.GitHub && login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

        return new ReviewerIdentity(job.ProviderHost, externalUserId, login, displayName, isBot);
    }

    private static ReviewPublicationContext BuildPublicationContext(
        ReviewJob job,
        PullRequest pr,
        ReviewRevision revision,
        ReviewerIdentity publicationIdentity,
        int? compareToIterationId)
    {
        object? providerSpecificContext = job.Provider == ScmProvider.AzureDevOps
            ? new AzureDevOpsPublicationContext(compareToIterationId)
            : null;

        return new ReviewPublicationContext(
            job.CodeReviewReference,
            revision,
            publicationIdentity,
            pr.ExistingThreads ?? [],
            providerSpecificContext);
    }

    private static bool IsReviewerOwnedAuthor(Guid? authorId, string? authorName, Guid? reviewerId, Guid? authorizedIdentityId, string? authorizedIdentityName)
    {
        if (authorId.HasValue)
        {
            if (reviewerId.HasValue && authorId.Value == reviewerId.Value)
            {
                return true;
            }

            if (authorizedIdentityId.HasValue && authorId.Value == authorizedIdentityId.Value)
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(authorizedIdentityName)
               && string.Equals(authorName, authorizedIdentityName, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountNonReviewerComments(
        IReadOnlyList<PrThreadComment> comments,
        Guid? reviewerId,
        Guid? authorizedIdentityId,
        string? authorizedIdentityName)
    {
        return comments.Count(comment => !IsReviewerOwnedAuthor(
            comment.AuthorId,
            comment.AuthorName,
            reviewerId,
            authorizedIdentityId,
            authorizedIdentityName));
    }

    private static ReviewThreadRef CreateReviewThreadRef(ReviewJob job, PrCommentThread thread)
    {
        return new ReviewThreadRef(
            job.CodeReviewReference,
            thread.ThreadId.ToString(CultureInfo.InvariantCulture),
            thread.FilePath,
            thread.LineNumber,
            true);
    }

    private async Task<ReviewRevision> ResolvePublicationReviewRevisionAsync(ReviewJob job, CancellationToken ct)
    {
        var reviewRevision = job.ReviewRevisionReference;
        if (job.Provider != ScmProvider.AzureDevOps && RequiresLiveRevisionRefresh(reviewRevision))
        {
            var latestRevision = await providerRegistry
                .GetCodeReviewQueryService(job.Provider)
                .GetLatestRevisionAsync(job.ClientId, job.CodeReviewReference, ct);

            if (latestRevision is not null)
            {
                logger.LogInformation(
                    "Refreshed invalid or missing review revision before publication for job {JobId} and provider {Provider}.",
                    job.Id,
                    job.Provider);
                return latestRevision;
            }
        }

        return ResolveReviewRevision(job);
    }

    private static bool RequiresLiveRevisionRefresh(ReviewRevision? revision)
    {
        if (revision is null)
        {
            return true;
        }

        return !LooksLikeCommitSha(revision.HeadSha)
               || !LooksLikeCommitSha(revision.BaseSha)
               || (revision.StartSha is not null && !LooksLikeCommitSha(revision.StartSha));
    }

    private static bool LooksLikeCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 7 or > 64)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static ReviewRevision ResolveReviewRevision(ReviewJob job)
    {
        if (job.ReviewRevisionReference is { } reviewRevision)
        {
            return reviewRevision;
        }

        if (job.Provider == ScmProvider.AzureDevOps)
        {
            var legacyRevisionId = job.IterationId.ToString(CultureInfo.InvariantCulture);
            return new ReviewRevision(
                $"ado-head-{legacyRevisionId}",
                $"ado-base-{legacyRevisionId}",
                null,
                legacyRevisionId,
                null);
        }

        throw new InvalidOperationException($"Review job {job.Id} is missing normalized review revision data for provider {job.Provider}.");
    }

    private sealed record ResolvedReviewerContext(ReviewerIdentity? ConfiguredTriggerReviewer, Guid? EffectiveReviewerId);
}
