// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
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
    IFileByFileReviewOrchestrator fileByFileReviewOrchestrator) : IReviewWorkflowRunner
{
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

            var reviewTools = reviewContextToolsFactory.Create(
                new ReviewContextToolsRequest(
                    fixture.PullRequestSnapshot.CodeReview,
                    pullRequest.SourceBranch,
                    job.IterationId,
                    job.ClientId,
                    ProviderScopePath: job.OrganizationUrl));

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

            var context = new ReviewSystemContext(null, relevantInstructions, reviewTools)
            {
                ExclusionRules = exclusionRules,
                ModelId = request.Configuration?.ModelSelection.ModelId ?? request.ModelId,
                Temperature = request.Configuration?.Temperature,
            };

            var result = await fileByFileReviewOrchestrator.ReviewAsync(
                job,
                pullRequest,
                context,
                cancellationToken,
                request.ChatClient);

            await jobs.SetResultAsync(job.Id, result, cancellationToken);

            var protocols = (await diagnosticsReader.GetJobProtocolAsync(job.Id, cancellationToken))?.Protocols ?? [];

            return new ReviewWorkflowResult(jobs.GetById(job.Id) ?? job, result, protocols);
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
            fixtureAccessor.Fixture = null;
        }
    }
}
