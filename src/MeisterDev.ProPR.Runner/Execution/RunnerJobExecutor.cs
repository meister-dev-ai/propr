// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.ProRV.Abstractions;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Runs one leased job: fetches the code, composes the review pipeline against the control plane, and
///     ships back what the review produced.
///     <para>
///         The pipeline this builds is the same one the control plane runs. Only its edges differ — the
///         model calls go through the relay, the credentialed tools go through the proxy, and the trace and
///         results go into the spool instead of into a database. Anywhere those substitutions changed
///         behaviour rather than destination, a review would mean one thing here and another there.
///     </para>
/// </summary>
public sealed partial class RunnerJobExecutor(
    IHttpClientFactory httpClients,
    WorkspaceFetcher workspaces,
    RunnerCredentialStore credentials,
    IOptions<AiReviewOptions> reviewOptions,
    IOptions<ReviewWorkspaceOptions> workspaceOptions,
    IProRVPrefilter proRvPrefilter,
    IStructuralCodeAnalyzer structuralAnalyzer,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<RunnerJobExecutor> logger) : IRunnerJobExecutor
{
    /// <summary>The named client every proxied call, relayed completion, and ingest batch goes out on.</summary>
    public const string ExecutionHttpClientName = "runner-execution";

    /// <inheritdoc />
    public async Task ExecuteAsync(RunnerJobManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using var http = httpClients.CreateClient(ExecutionHttpClientName);

        // Everything this job says goes to the replica that granted its lease, when the manifest names
        // one: the registries its calls are answered from live in that replica's process. The work loop
        // validated the address before starting the job.
        if (!string.IsNullOrWhiteSpace(manifest.ServedBy))
        {
            http.BaseAddress = RunnerReplicaAffinity.Resolve(manifest.ServedBy, "runners/execution/");
        }

        var spool = new JobSpool(http, manifest.JobId, manifest.LeaseGeneration, loggerFactory.CreateLogger<JobSpool>());

        try
        {
            var lease = await workspaces.FetchAsync(manifest, credentials.Current ?? string.Empty, ct);
            await using var workspace = new GitReviewRepositoryWorkspace(
                lease,
                new GitCommandRunner(loggerFactory.CreateLogger<GitCommandRunner>()),
                loggerFactory.CreateLogger<GitReviewRepositoryWorkspace>(),
                new ReviewWorkspaceCleanupService(workspaceOptions, loggerFactory.CreateLogger<ReviewWorkspaceCleanupService>()));

            var job = RunnerReviewSubject.BuildJob(manifest);
            await this.SeedPriorResultsAsync(http, manifest, job, ct);

            var pullRequest = await RunnerReviewSubject.BuildPullRequestAsync(manifest, workspace, ct);
            LogReviewing(logger, manifest.JobId, pullRequest.ChangedFiles.Count);

            var result = await this.RunPipelineAsync(manifest, job, pullRequest, workspace, http, spool, ct);

            // The spool goes first, and the order is not a preference. Submitting findings publishes the
            // review and moves the job to a terminal state, and ingest refuses a job that is no longer
            // executing — so findings-then-flush loses the whole trace, every file result, and every
            // spend record of a review that succeeded. It cost a completed review showing zero tokens
            // and zero cost to notice.
            await FlushUntilEmptyAsync(spool);
            await this.SubmitFindingsAsync(http, manifest, result, ct);
        }
        finally
        {
            // Again on the way out, for a review that ended before the flush above: what it did finish is
            // what a later attempt resumes from, and discarding it would make every interruption start
            // over. A no-op when the spool is already empty.
            await FlushUntilEmptyAsync(spool);

            // Whatever happened, the code this host read does not stay on it.
            workspaces.Purge(manifest.JobId);
        }
    }

    private async Task<ReviewResult> RunPipelineAsync(
        RunnerJobManifest manifest,
        ReviewJob job,
        PullRequest pullRequest,
        IReviewRepositoryWorkspace workspace,
        HttpClient http,
        JobSpool spool,
        CancellationToken ct)
    {
        var recorder = new SpoolingProtocolRecorder(spool, timeProvider);
        var results = new SpoolingFileResultStore(job, spool);

        // One latch for the whole job, marked by whichever relayed completion first reports the soft cap
        // or meets a refusal. Every relay client shares it, because the budget is the job's, not a role's.
        var budgetSignal = new RunnerBudgetSignal();
        IChatClient Relay(string role) => new RelayChatClient(http, manifest.JobId, manifest.LeaseGeneration, role, budgetSignal);

        var models = new RelayLogicalModelResolver(manifest, Relay);

        // The pipeline asks this for each stage's model, and uses the answer for the client to call, the
        // model id it records, the tokenizer it counts prompts with, and the context window it budgets
        // against. Without it every one of those is null and the review runs on the default client, blind:
        // real token totals recorded against a protocol naming no model, and no spend shipped at all.
        var runtimes = new RelayAiRuntimeResolver(manifest, Relay);

        var defaultClient = new RelayChatClient(
            http,
            manifest.JobId,
            manifest.LeaseGeneration,
            manifest.DefaultModel.LogicalModelName,
            budgetSignal);

        var context = this.BuildContext(manifest, job, pullRequest, workspace, http, recorder, defaultClient);

        var memory = new ProxyThreadMemoryService(
            http,
            manifest.JobId,
            manifest.LeaseGeneration,
            recorder,
            loggerFactory.CreateLogger<ProxyThreadMemoryService>());

        using var pipeline = RunnerReviewPipeline.Compose(
            reviewOptions,
            recorder,
            results,
            defaultClient,
            runtimes,
            models,
            memory,
            proRvPrefilter,
            structuralAnalyzer,
            new ManifestLicensing(manifest),
            () => budgetSignal.Exhausted,
            loggerFactory);

        await RecordAbsentCollaboratorsAsync(recorder, manifest.JobId, pipeline.Report, ct);

        // Flushed as the review goes rather than only at the end, so an interrupted job leaves the control
        // plane with the files it did finish and the trace up to the interruption.
        using var pump = new Timer(
            _ =>
            {
                if (spool.ShouldFlush && !spool.RefusedForGood)
                {
                    _ = spool.FlushAsync(ct);
                }
            },
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        return await pipeline.Orchestrator.ReviewAsync(job, pullRequest, context, ct);
    }

    /// <summary>
    ///     Puts each deliberately-absent collaborator on the review's trace, under its own short protocol.
    ///     A remote review that ran without the licensing clamp, the soft-cap wind-down, or PR-wide passes
    ///     has to say so where an operator reads reviews — a log line on a host nobody tails is not saying it.
    /// </summary>
    private static async Task RecordAbsentCollaboratorsAsync(
        IProtocolRecorder recorder,
        Guid jobId,
        IReadOnlyList<RunnerCompositionEntry> report,
        CancellationToken ct)
    {
        var absences = report.Where(entry => entry.Disposition == RunnerCompositionDisposition.Absent).ToList();
        if (absences.Count == 0)
        {
            return;
        }

        var protocolId = await recorder.BeginAsync(jobId, attemptNumber: 1, label: "runner-composition", ct: ct);
        foreach (var absence in absences)
        {
            await recorder.RecordReviewStrategyEventAsync(
                protocolId,
                "runner_collaborator_absent",
                JsonSerializer.Serialize(new { collaborator = absence.Parameter, consequence = absence.Note }),
                null,
                null,
                ct);
        }

        await recorder.SetCompletedAsync(protocolId, "Completed", 0, 0, 0, 0, null, ct);
    }

    /// <summary>
    ///     The review context, built from the manifest the way the control plane builds it from the
    ///     database. Each value here has a counterpart there, and the manifest exists so the two agree.
    /// </summary>
    private ReviewSystemContext BuildContext(
        RunnerJobManifest manifest,
        ReviewJob job,
        PullRequest pullRequest,
        IReviewRepositoryWorkspace workspace,
        HttpClient http,
        IProtocolRecorder recorder,
        RelayChatClient defaultClient)
    {
        var toolsRequest = new ReviewContextToolsRequest(
            job.CodeReviewReference,
            pullRequest.SourceBranch,
            job.IterationId,
            job.ClientId,
            null,
            job.OrganizationUrl,
            pullRequest.TargetBranch,
            [.. pullRequest.ChangedFiles.Select(ChangedPathSnapshot.FromChangedFile)],
            Workspace: workspace,
            WorkspaceLease: workspace.Lease);

        // The credentialed tools go over the proxy; the twelve that read the working copy stay local, which
        // is what keeps a review from becoming network traffic. The local set is handed a disabled
        // code-knowledge gateway because the proxy answers those two, and a second client here would need
        // the credential this host does not have. The structural analyzer rides along so reference and
        // definition lookups parse the worktrees instead of answering "unavailable" — an answer the model
        // reads as "this repository has no cross-file references", which is a false negative, not a shrug.
        var tools = new ProxyReviewContextTools(
            new RunnerCallContext(manifest.JobId, manifest.LeaseGeneration, string.Empty),
            new HttpRunnerToolProxy(http),
            new LocalGitReviewContextTools(
                workspace,
                new DisabledProCursorGateway(),
                reviewOptions,
                toolsRequest,
                loggerFactory.CreateLogger<LocalGitReviewContextTools>(),
                structuralAnalyzer));

        return new ReviewSystemContext(
            manifest.Prompts.Overrides.GetValueOrDefault("customSystemMessage"),
            [
                .. manifest.RepositoryInstructions.Select(instruction => new RepositoryInstruction(
                    instruction.FileName,
                    instruction.Description,
                    instruction.WhenToUse,
                    instruction.Body))
            ],
            tools)
        {
            DefaultReviewChatClient = defaultClient,
            DefaultReviewModelId = manifest.DefaultModel.RemoteModelId,
            ModelId = manifest.DefaultModel.RemoteModelId,
            LogicalModelName = manifest.DefaultModel.LogicalModelName,
            MaxContextTokens = manifest.DefaultModel.MaxContextTokens,
            TokenizerName = manifest.DefaultModel.TokenizerName,
            ProtocolRecorder = recorder,
            ReviewWorkspace = workspace,
            // Only what the manifest can vouch for. Managed sessions, background responses, and cache
            // routing stay off: the relay serves whole completions only. Prompt caching is real — the
            // provider behind the relay caches or not regardless of which side composed the prompt — so
            // reporting it unsupported here would mislabel every remote cache hit as provider_unsupported.
            RuntimeCapabilities = new AgentReviewRuntimeCapabilities(
                SupportsProviderManagedSessions: false,
                SupportsManagedRemoteConversation: false,
                SupportsBackgroundResponses: false,
                PrefersResponsesApi: false,
                SupportsPromptCaching: manifest.DefaultModel.SupportsPromptCaching),
            OutputLanguage = manifest.Prompts.Language,
            ExclusionRules = ReviewExclusionRules.FromPatterns(manifest.Exclusions),
            PromptOverrides = manifest.Prompts.Overrides,
            BaselineReasoningEffort = ParseEffort(manifest.Prompts.Aggressiveness),
            ReviewPasses =
            [
                .. manifest.Passes.Select(pass => new ReviewPassSpec(
                    Guid.Empty,
                    pass.Lens,
                    pass.Scope,
                    pass.Shadow,
                    ParseEffort(pass.Model.ReasoningEffort),
                    pass.Model.LogicalModelName))
            ],

            // The per-client decisions, carried because a runner has no client record to read them from.
            // Without them every one falls to its default and the review quietly becomes a different
            // review — most visibly the pass list above, which does nothing at all unless the union is on.
            EnableMultiPassUnion = manifest.Behaviour?.EnableMultiPassUnion ?? false,
            EnableLanguageRobustScreening = manifest.Behaviour?.EnableLanguageRobustScreening ?? false,
            EnableEvidenceBackedVerification = manifest.Behaviour?.EnableEvidenceBackedVerification ?? false,
            // Each fallback is the property's own default, so a manifest without this section leaves the
            // review behaving exactly as it did before the section existed. Linked items default on.
            IncludeLinkedItemsInContext = manifest.Behaviour?.IncludeLinkedItemsInContext ?? true,
            Temperature = manifest.Behaviour?.Temperature,
        };
    }

    /// <summary>
    ///     Hands the findings back, in one chunk. Splitting is the ingest contract's affordance for a review
    ///     too large for one request, and is not exercised yet: nothing here has produced a submission that
    ///     needed it, and a chunker without a case to size it against would be guesswork.
    /// </summary>
    private async Task SubmitFindingsAsync(
        HttpClient http,
        RunnerJobManifest manifest,
        ReviewResult result,
        CancellationToken ct)
    {
        var request = new
        {
            jobId = manifest.JobId,
            leaseGeneration = manifest.LeaseGeneration,
            contractVersion = RunnerContractVersion.Current,
            submissionId = manifest.JobId.ToString("N"),
            chunkIndex = 0,
            chunkCount = 1,
            summary = result.Summary,
            comments = result.Comments,

            // What the review says about itself travels with it, or a soft-capped or context-degraded
            // remote review reads as complete everywhere the labels are read.
            annotations = new
            {
                carriedForwardFilePaths = result.CarriedForwardFilePaths,
                carriedForwardCandidatesSkipped = result.CarriedForwardCandidatesSkipped,
                contextDegradedFilePaths = result.ContextDegradedFilePaths,
                contextSkippedFilePaths = result.ContextSkippedFilePaths,
                budgetSoftCapped = result.BudgetSoftCapped,
                budgetSoftCapThresholdUsd = result.BudgetSoftCapThresholdUsd,
                budgetSoftCapSpentUsd = result.BudgetSoftCapSpentUsd,
                budgetSoftCapSkippedFilePaths = result.BudgetSoftCapSkippedFilePaths,
            },
        };

        using var response = await http.PostAsJsonAsync("findings", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Thrown, so the loop hands the lease back and the job is picked up again. Losing the findings
            // silently would leave a review that says it completed and posted nothing.
            throw new InvalidOperationException($"The control plane refused this job's findings with {(int)response.StatusCode}.");
        }

        LogSubmitted(logger, manifest.JobId, result.Comments.Count);
    }

    /// <summary>
    ///     Reads back what this job already had reviewed and seeds it into the in-memory job, so a reclaimed
    ///     review resumes where it stopped and synthesizes over everything rather than over its own second
    ///     half.
    ///     <para>
    ///         Fail-soft. A read that does not answer costs the job its resume — every file is reviewed
    ///         again — which is worse than resuming and better than refusing to run at all.
    ///     </para>
    /// </summary>
    private async Task SeedPriorResultsAsync(
        HttpClient http,
        RunnerJobManifest manifest,
        ReviewJob job,
        CancellationToken ct)
    {
        PriorResultsEnvelope? prior;
        try
        {
            using var response = await http.PostAsJsonAsync(
                "prior-results",
                new { jobId = manifest.JobId, leaseGeneration = manifest.LeaseGeneration, contractVersion = RunnerContractVersion.Current },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                LogPriorResultsUnavailable(logger, manifest.JobId, $"the control plane answered {(int)response.StatusCode}");
                return;
            }

            prior = await response.Content.ReadFromJsonAsync<PriorResultsEnvelope>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogPriorResultsUnavailable(logger, manifest.JobId, ex.Message);
            return;
        }

        if (prior?.Value is not { Count: > 0 } recorded)
        {
            return;
        }

        RunnerReviewSubject.SeedPriorResults(job, recorded);
        LogResumed(logger, manifest.JobId, recorded.Count);
    }

    /// <summary>
    ///     Ships what is left, retrying a failed batch a few times before giving up on it. A batch that
    ///     cannot be shipped is a hole in the trace and a file the control plane will not know finished.
    ///     <para>
    ///         Deliberately not on the job's cancellation token. This runs when the job is already over,
    ///         including when it was cancelled, and a flush that cancelled with it would throw away exactly
    ///         what a drained job most needs to leave behind.
    ///     </para>
    /// </summary>
    private async Task FlushUntilEmptyAsync(JobSpool spool)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var attempt = 0; attempt < 3 && !deadline.IsCancellationRequested; attempt++)
        {
            if (await spool.FlushAsync(deadline.Token))
            {
                break;
            }

            // The control plane says this job is not ours to write to. Retrying is a loop with a known
            // answer, so the attempts are spent on nothing.
            if (spool.RefusedForGood)
            {
                LogFlushAbandoned(logger, spool.JobId);
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // Said once, at the end, rather than per drop: a trace missing events has to say so, and a warning
        // per dropped event would itself be the flood the ceiling exists to survive.
        if (spool.DroppedEvents > 0)
        {
            LogTraceTruncated(logger, spool.JobId, spool.DroppedEvents);
        }
    }

    private static ReviewReasoningEffort ParseEffort(string? effort)
    {
        return Enum.TryParse<ReviewReasoningEffort>(effort, ignoreCase: true, out var parsed)
            ? parsed
            : ReviewReasoningEffort.None;
    }

    [LoggerMessage(EventId = 6401, Level = LogLevel.Information, Message = "Reviewing job {JobId}: {FileCount} changed files in scope")]
    private static partial void LogReviewing(ILogger logger, Guid jobId, int fileCount);

    [LoggerMessage(EventId = 6402, Level = LogLevel.Information, Message = "Job {JobId} returned {FindingCount} findings to the control plane")]
    private static partial void LogSubmitted(ILogger logger, Guid jobId, int findingCount);

    [LoggerMessage(
        EventId = 6406,
        Level = LogLevel.Warning,
        Message = "Stopped shipping job {JobId}: the control plane no longer accepts writes for it")]
    private static partial void LogFlushAbandoned(ILogger logger, Guid jobId);

    [LoggerMessage(
        EventId = 6407,
        Level = LogLevel.Warning,
        Message = "Job {JobId} dropped {DroppedEvents} trace event(s) at the spool ceiling; its trace is incomplete")]
    private static partial void LogTraceTruncated(ILogger logger, Guid jobId, int droppedEvents);

    [LoggerMessage(EventId = 6403, Level = LogLevel.Information, Message = "Job {JobId} resumed {ResultCount} file results recorded by an earlier attempt")]
    private static partial void LogResumed(ILogger logger, Guid jobId, int resultCount);

    [LoggerMessage(
        EventId = 6404,
        Level = LogLevel.Warning,
        Message = "Job {JobId} could not read what an earlier attempt reviewed and will review every file again: {Reason}")]
    private static partial void LogPriorResultsUnavailable(ILogger logger, Guid jobId, string reason);

    private sealed record PriorResultsEnvelope(IReadOnlyList<RunnerPriorFileResult>? Value, bool Unavailable);
}
