// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Replays one offline review fixture through the shared file-by-file review workflow.
/// </summary>
public sealed class ReviewWorkflowRunner(
    IJobRepository jobRepository,
    IReviewJobExecutionStore jobs,
    IReviewDiagnosticsReader diagnosticsReader,
    IReviewEvaluationFixtureAccessor fixtureAccessor,
    IReviewEvaluationFixtureValidator fixtureValidator,
    IPullRequestFetcher pullRequestFetcher,
    IReviewContextToolsFactory reviewContextToolsFactory,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    IFileByFileReviewOrchestrator fileByFileReviewOrchestrator,
    IReviewRepositoryWorkspaceManager? workspaceManager = null,
    IEnumerable<BoundaryIssueReport>? boundaryIssues = null,
    IOfflineTierModelAccessor? tierModelAccessor = null) : IReviewWorkflowRunner
{
    private readonly IReadOnlyList<BoundaryIssueReport> _boundaryIssues = boundaryIssues?.ToList() ?? [];

    /// <inheritdoc />
    public async Task<ReviewWorkflowResult> RunAsync(
        ReviewWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fixture is null)
        {
            throw new InvalidOperationException("A review evaluation fixture is required for offline workflow execution.");
        }

        var fixture = request.Fixture;
        var job = request.Job;

        try
        {
            fixtureAccessor.Fixture = fixture;
            fixtureAccessor.ScenarioId = fixture.ActiveScenarioIdOrNull;
            await fixtureValidator.ValidateAsync(fixture, cancellationToken);

            job.SetPrContext(
                fixture.PullRequestSnapshot.Title,
                fixture.RepositorySnapshot.RepositoryName,
                fixture.PullRequestSnapshot.SourceBranch,
                fixture.PullRequestSnapshot.TargetBranch);
            job.SetReviewRevision(fixture.PullRequestSnapshot.Revision);

            if (string.Equals(
                    job.RepositoryId,
                    fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
                    StringComparison.Ordinal)
                && job.PullRequestId == fixture.PullRequestSnapshot.CodeReview.Number)
            {
                job.SetProviderReviewContext(fixture.PullRequestSnapshot.CodeReview);
            }

            if (jobRepository.GetById(job.Id) is null)
            {
                await jobRepository.AddAsync(job, cancellationToken);
            }

            await jobs.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, cancellationToken);

            var pullRequest = await pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                cancellationToken);

            var workspacePreparation = workspaceManager is null
                ? new ReviewRepositoryWorkspacePreparationResult(null, null)
                : await workspaceManager.PrepareAsync(
                    new ReviewRepositoryWorkspaceRequest(
                        job.Id,
                        job.ClientId,
                        fixture.PullRequestSnapshot.CodeReview.Repository.Host.Provider,
                        job.OrganizationUrl,
                        fixture.PullRequestSnapshot.CodeReview.Repository,
                        job.PullRequestId,
                        job.ReviewRevisionReference ?? fixture.PullRequestSnapshot.Revision,
                        pullRequest.SourceBranch,
                        pullRequest.TargetBranch,
                        pullRequest.ChangedFiles.Select(ChangedPathSnapshot.FromChangedFile).ToList().AsReadOnly()),
                    cancellationToken);

            await this.RecordWorkspaceProtocolAsync(job, workspacePreparation, cancellationToken);

            var reviewTools = reviewContextToolsFactory.Create(
                new ReviewContextToolsRequest(
                    fixture.PullRequestSnapshot.CodeReview,
                    pullRequest.SourceBranch,
                    job.IterationId,
                    job.ClientId,
                    job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                        ? job.ProCursorSourceIds
                        : null,
                    job.OrganizationUrl,
                    pullRequest.TargetBranch,
                    pullRequest.ChangedFiles.Select(ChangedPathSnapshot.FromChangedFile).ToList().AsReadOnly(),
                    Workspace: workspacePreparation.Workspace,
                    WorkspaceLease: workspacePreparation.Workspace?.Lease,
                    WorkspaceFailure: workspacePreparation.Failure));

            var changedFilePaths = pullRequest.ChangedFiles.Select(file => file.Path).ToList();
            var fetchedInstructions = await instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                cancellationToken);
            var relevantInstructions = fetchedInstructions.Count > 0
                ? await instructionEvaluator.EvaluateRelevanceAsync(
                    fetchedInstructions,
                    changedFilePaths,
                    cancellationToken)
                : [];

            var exclusionRules = await exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                cancellationToken);

            var promptExperimentContext = request.PromptExperiment ?? new PromptExperimentContext("baseline", skippedSteps: request.EffectiveSkippedSteps);

            var context = new ReviewSystemContext(null, relevantInstructions, reviewTools)
            {
                ExclusionRules = exclusionRules,
                ModelId = request.Configuration?.ModelSelection.ModelId ?? request.ModelId,
                Temperature = request.Configuration?.Temperature,
                EnableEvidenceBackedVerification = request.Configuration?.EnableEvidenceBackedVerification ?? false,
                EnableLanguageRobustScreening = request.Configuration?.EnableLanguageRobustScreening ?? false,
                EnableMultiPassUnion = request.Configuration?.EnableMultiPassUnion ?? false,
                MultiPassUnionPassCount = request.Configuration?.MultiPassUnionPassCount,
                MultiPassDiversity = request.Configuration?.MultiPassDiversity,
                PromptExperiment = promptExperimentContext,
                SkippedSteps = request.EffectiveSkippedSteps,
                ReviewWorkspace = workspacePreparation.Workspace,
            };

            // Activate per-purpose model selection for this run when the configuration defines tiered models,
            // so the offline runtime resolver routes each tier/triage to its configured model. Left null
            // otherwise, which keeps single-model behavior.
            var tieredModels = request.Configuration?.ModelSelection.TieredModels;
            if (tierModelAccessor is not null && tieredModels is not null)
            {
                tierModelAccessor.Selection = new OfflineTierModelSelection(
                    request.ChatClient,
                    tieredModels,
                    request.Configuration?.ModelSelection.ModelId);
            }

            if (!string.IsNullOrWhiteSpace(request.PipelineProfileId))
            {
                job.SetReviewPipelineProfile(request.PipelineProfileId);
            }

            ReviewResult result;
            try
            {
                result = await fileByFileReviewOrchestrator.ReviewAsync(
                    job,
                    pullRequest,
                    context,
                    cancellationToken,
                    request.ChatClient);
            }
            finally
            {
                if (workspacePreparation.Workspace is not null)
                {
                    await workspacePreparation.Workspace.DisposeAsync();
                    context.ReviewWorkspace = null;
                }
            }

            await jobs.SetResultAsync(job.Id, result, cancellationToken);

            var protocols = (await diagnosticsReader.GetJobProtocolAsync(job.Id, true, cancellationToken))?.Protocols ?? [];

            return new ReviewWorkflowResult(
                jobs.GetById(job.Id) ?? job,
                result,
                protocols,
                this._boundaryIssues);
        }
        catch (Exception ex)
        {
            if (jobRepository.GetById(job.Id) is not null)
            {
                await jobs.SetFailedAsync(job.Id, ex.Message, cancellationToken);
            }

            throw;
        }
        finally
        {
            fixtureAccessor.ScenarioId = null;
            fixtureAccessor.Fixture = null;
            if (tierModelAccessor is not null)
            {
                tierModelAccessor.Selection = null;
            }
        }
    }

    private async Task RecordWorkspaceProtocolAsync(
        ReviewJob? job,
        ReviewRepositoryWorkspacePreparationResult workspacePreparation,
        CancellationToken ct)
    {
        _ = job;
        _ = workspacePreparation;
        await Task.CompletedTask;
        _ = ct;
    }
}
