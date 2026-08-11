// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Diagnostics;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Resolves the job manifest from client configuration at dispatch.
///     <para>
///         Every value here is read exactly once. The in-process pipeline reads the same values from the
///         same accessors as it goes, which is why a parity test is worth having: the two must agree, or a
///         review means one thing locally and another remotely.
///     </para>
/// </summary>
public sealed partial class RunnerJobManifestResolver(
    IClientRegistry clientRegistry,
    IRepositoryExclusionFetcher exclusionFetcher,
    IRepositoryInstructionFetcher instructionFetcher,
    IRepositoryInstructionEvaluator instructionEvaluator,
    ILogger<RunnerJobManifestResolver> logger,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    ILogicalModelResolver? logicalModelResolver = null,
    IPromptOverrideService? promptOverrideService = null,
    IBudgetCapsProvider? budgetCapsProvider = null,
    IReviewSpendAccumulator? spendAccumulator = null,
    IScmProviderRegistry? providerRegistry = null,
    AiReviewOptions? reviewOptions = null,
    ILicensingCapabilityService? licensing = null,
    IReviewJobExecutionStore? executionStore = null) : IRunnerJobManifestResolver
{
    /// <inheritdoc />
    public async Task<RunnerJobManifestResolution> ResolveAsync(
        RunnerJobManifestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = request.Job;

        // A revision the executor cannot pin is not reviewable: it would fetch whatever the branch points
        // at, which is not the change the review was requested for.
        if (string.IsNullOrWhiteSpace(job.RevisionHeadSha) || string.IsNullOrWhiteSpace(job.RevisionBaseSha))
        {
            return RunnerJobManifestResolution.Refused(
                "The review job has no resolved head and base commit, so there is no revision to pin an "
                + "out-of-process execution to.");
        }

        try
        {
            var defaultModel = await this.ResolveDefaultModelAsync(job.ClientId, ct);
            if (defaultModel is null)
            {
                return RunnerJobManifestResolution.Refused(
                    "The client's default review model is not a named logical model, and a runner can only be "
                    + "given a name to relay: the connection behind it stays on the control plane. Bind the "
                    + "review purpose to a logical model to review this client's work out of process.");
            }

            var (defaultBinding, defaultConnectionId) = defaultModel.Value;

            // The same stamp the in-process path writes at review start. Ingested spend is priced through
            // job.AiConnectionId and the overview reads the model off the job. Left unstamped, a remote
            // review's tokens are priced against nothing, and the job reads as if no model had served it.
            if (executionStore is not null)
            {
                await executionStore.UpdateAiConfigAsync(
                    job.Id,
                    defaultConnectionId,
                    defaultBinding.RemoteModelId,
                    ct,
                    job.ReviewTemperature);
            }

            var passes = await clientRegistry.GetReviewPassesAsync(job.ClientId, ct);

            // The executor composes no PR-wide generator yet, so a job whose pass list publishes from a
            // pr_wide entry would review less than it was asked to, without reporting it. Refused the same
            // way as a connection-resolved model: at dispatch, naming what to change. A shadow entry still
            // dispatches, because it publishes nothing, so skipping it changes telemetry and not the review.
            if (passes.Any(pass => !pass.Shadow && string.Equals(pass.Scope, ReviewPassScope.PrWide, StringComparison.Ordinal)))
            {
                return RunnerJobManifestResolution.Refused(
                    "The client's pass list contains a publishing pr_wide-scope entry, which does not run on a "
                    + "runner yet. Make the entry shadow, remove it, or keep this client's reviews in process.");
            }

            var passBindings = await this.ResolvePassModelsAsync(job.ClientId, passes, ct);
            var baselineEffort = await clientRegistry.GetBaselineReasoningEffortAsync(job.ClientId, ct);
            var outputLanguage = await clientRegistry.GetOutputLanguageAsync(job.ClientId, ct);
            var customSystemMessage = await clientRegistry.GetCustomSystemMessageAsync(job.ClientId, ct);

            var exclusions = await exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                request.TargetBranch,
                job.ClientId,
                ct);

            var fetchedInstructions = await instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                request.TargetBranch,
                job.ClientId,
                ct);

            // Relevance is decided here for the same reason everything else is: it reads the repository and
            // the changed set, and the in-process path narrows the instructions the same way before use.
            var instructions = fetchedInstructions.Count > 0
                ? await instructionEvaluator.EvaluateRelevanceAsync(fetchedInstructions, request.ChangedPaths, ct)
                : [];

            var overrides = await this.ResolvePromptOverridesAsync(job.ClientId, ct);
            var headroom = await this.ResolveBudgetHeadroomAsync(job, ct);

            // Read once and used twice: the flag is carried in the manifest for the executor's tool gating, and it
            // decides here whether linked items are discovered at all.
            var includeLinkedItems = await clientRegistry.GetIncludeLinkedItemsInContextEnabledAsync(job.ClientId, ct);
            var linkedItems = includeLinkedItems
                ? await this.DiscoverLinkedItemsAsync(job, request.Conversation, ct)
                : null;

            var manifest = new RunnerJobManifest(
                RunnerContractVersion.Current,
                job.Id,
                job.ClientId,
                request.Lease.Generation,
                new RunnerReviewTarget(
                    job.Provider.ToString(),
                    job.OrganizationUrl,
                    job.ProjectId,
                    job.RepositoryId,
                    job.PrRepositoryName ?? job.RepositoryId,
                    job.ExternalCodeReviewId ?? job.PullRequestId.ToString(),
                    job.PullRequestId,
                    job.IterationId,
                    job.PrTitle ?? string.Empty,
                    request.Description,
                    job.PrSourceBranch ?? string.Empty,
                    request.TargetBranch,
                    job.RevisionHeadSha,
                    job.RevisionBaseSha,
                    [.. request.ChangedPaths],
                    ToManifestThreads(request.ExistingThreads)),
                new RunnerWorkspaceReference(
                    request.WorkspaceFetchPath,
                    job.RevisionHeadSha,
                    job.RevisionBaseSha,
                    request.MaxWorkspaceTransferBytes),
                defaultBinding,
                passBindings,
                new RunnerPromptConfiguration(
                    outputLanguage,
                    // The baseline reasoning effort is what an unlisted pass runs at, and the custom system
                    // message is prompt configuration too: both belong to the prompt, not the model binding.
                    baselineEffort.ToString(),
                    MergeSystemMessage(overrides, customSystemMessage)),
                [.. exclusions.Patterns],
                [
                    .. instructions.Select(instruction => new RunnerRepositoryInstruction(
                        instruction.FileName,
                        instruction.Description,
                        instruction.WhenToUse,
                        instruction.Body))
                ],
                headroom,
                CurrentTraceContext(),

                // Read from the same accessors the in-process path reads, at the same point in the job's
                // life, so the two cannot answer differently for the same client.
                new RunnerReviewBehaviour(
                    await clientRegistry.GetMultiPassUnionEnabledAsync(job.ClientId, ct),
                    await clientRegistry.GetLanguageRobustScreeningEnabledAsync(job.ClientId, ct),
                    await clientRegistry.GetEvidenceBackedVerificationEnabledAsync(job.ClientId, ct),
                    includeLinkedItems,
                    job.ReviewTemperature,
                    job.ReviewPipelineProfileId),
                linkedItems,

                // Resolved here and not on the executor because the license lives in this database. A host
                // with no licensing service at all (the offline harness) reads as licensed, exactly as the
                // in-process planner treats a null licensing service.
                ParallelReviewExecutionLicensed: licensing is null
                                                 || await licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, ct));

            return RunnerJobManifestResolution.Resolved(manifest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refused rather than partly filled. An executor cannot tell a value that failed to resolve
            // from one deliberately left empty, so it would review under configuration that was never
            // chosen.
            LogResolutionFailed(logger, job.Id, ex);
            return RunnerJobManifestResolution.Refused($"Resolving the job manifest failed, so the job was not offered: {ex.Message}");
        }
    }

    /// <summary>
    ///     Maps the configured pass list onto the wire shape. A pass names a logical model; a pass that
    ///     still binds a concrete configured model cannot be executed remotely, because resolving that
    ///     binding is the control plane's job and naming it is how the key stays there.
    /// </summary>
    private async Task<IReadOnlyList<RunnerReviewPass>> ResolvePassModelsAsync(
        Guid clientId,
        IReadOnlyList<ReviewPassSpec> passes,
        CancellationToken ct)
    {
        var resolved = new List<RunnerReviewPass>(passes.Count);
        for (var index = 0; index < passes.Count; index++)
        {
            var pass = passes[index];
            if (string.IsNullOrWhiteSpace(pass.LogicalModelName))
            {
                throw new InvalidOperationException(
                    $"Review pass {index + 1} binds a configured model directly rather than naming a logical "
                    + "model, and only a named model can be resolved on the control-plane side.");
            }

            if (logicalModelResolver is null)
            {
                throw new InvalidOperationException(
                    "No logical-model resolver is available, so the pass list's models cannot be described "
                    + "to an executor.");
            }

            var runtime = await logicalModelResolver.ResolveChatRuntimeAsync(clientId, pass.LogicalModelName, ct: ct);
            resolved.Add(
                new RunnerReviewPass(
                    index + 1,
                    Describe(pass.LogicalModelName, runtime.Runtime, runtime.ReasoningEffort),
                    pass.Lens,
                    pass.Scope,
                    pass.Shadow));
        }

        return resolved;
    }

    /// <summary>
    ///     The model every stage that does not name a pass of its own runs on.
    ///     <para>
    ///         Null when the client's review purpose resolves to a raw connection rather than a named role.
    ///         The relay routes by name and nothing else, so there would be nothing for the executor to ask
    ///         for; the caller turns that into a refusal naming what to configure.
    ///     </para>
    /// </summary>
    private async Task<(RunnerModelBinding Binding, Guid ConnectionId)?> ResolveDefaultModelAsync(
        Guid clientId,
        CancellationToken ct)
    {
        if (aiRuntimeResolver is null)
        {
            return null;
        }

        var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(clientId, AiPurpose.ReviewDefault, ct);
        return string.IsNullOrWhiteSpace(runtime.LogicalModelName)
            ? null
            : (Describe(runtime.LogicalModelName!, runtime, ReviewReasoningEffort.None), runtime.Connection.Id);
    }

    /// <summary>
    ///     Everything about a resolved model that is safe to send and that the pipeline needs before it
    ///     makes a call. The connection, its credential, and the model's pricing stay here.
    /// </summary>
    private static RunnerModelBinding Describe(
        string logicalModelName,
        IResolvedAiChatRuntime runtime,
        ReviewReasoningEffort effort)
    {
        return new RunnerModelBinding(
            logicalModelName,
            runtime.Model.RemoteModelId,
            runtime.Connection.ProviderKind.ToString(),
            effort.ToString(),
            runtime.Model.TokenizerName,
            runtime.Model.MaxInputTokens,
            runtime.Model.MaxContextTokens,
            runtime.Model.SupportsPromptCaching,
            runtime.Model.SupportsToolUse,
            runtime.Model.SupportsStructuredOutput);
    }

    /// <summary>
    ///     The work items linked to the review, discovered and bounded exactly the way the in-process path
    ///     does at review start, because discovery is a credentialed provider call the executor cannot make.
    ///     <para>
    ///         Fail-soft on its own, unlike the rest of this resolver: the in-process path reviews without
    ///         linked items when discovery fails, so refusing the whole dispatch here would make the remote
    ///         path stricter than the local one for the same failure.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<RunnerLinkedItem>?> DiscoverLinkedItemsAsync(
        ReviewJob job,
        PullRequest? conversation,
        CancellationToken ct)
    {
        if (providerRegistry is null || reviewOptions is null || conversation is null)
        {
            return null;
        }

        try
        {
            var provider = providerRegistry.GetLinkedItemProvider(job.Provider);
            var discovered = await provider.DiscoverLinkedItemsAsync(job.ClientId, conversation, ct);
            if (discovered.Count == 0)
            {
                return null;
            }

            var bounded = LinkedItemContextBounding.Bound(
                discovered,
                reviewOptions.MaxLinkedItemsInContext,
                reviewOptions.MaxLinkedItemDescriptionChars,
                out _);

            return bounded.Count == 0
                ? null
                :
                [
                    .. bounded.Select(item => new RunnerLinkedItem(
                        item.ProviderKey,
                        item.ItemType,
                        item.Title,
                        item.Description,
                        item.Url,
                        [.. item.RelatedLinks.Select(link => new RunnerLinkedItemRef(link.Kind, link.TargetKey, link.Url, link.Title))]))
                ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLinkedItemsSkipped(logger, job.Id, ex);
            return null;
        }
    }

    private static IReadOnlyList<RunnerReviewThread> ToManifestThreads(IReadOnlyList<PrCommentThread>? threads)
    {
        if (threads is null || threads.Count == 0)
        {
            return [];
        }

        return
        [
            .. threads.Select(thread => new RunnerReviewThread(
                thread.FilePath,
                thread.LineNumber,
                thread.Status,
                [.. thread.Comments.Select(comment => new RunnerReviewThreadComment(comment.AuthorName, comment.Content))]))
        ];
    }

    private static IReadOnlyDictionary<string, string> MergeSystemMessage(
        IReadOnlyDictionary<string, string> overrides,
        string? customSystemMessage)
    {
        if (string.IsNullOrWhiteSpace(customSystemMessage))
        {
            return overrides;
        }

        var merged = new Dictionary<string, string>(overrides, StringComparer.Ordinal)
        {
            ["customSystemMessage"] = customSystemMessage,
        };
        return merged;
    }

    /// <summary>
    ///     The ambient trace context, so the executor's spans join this review's trace instead of starting
    ///     one of their own and leaving the two halves unrelatable.
    /// </summary>
    private static RunnerTraceContext CurrentTraceContext()
    {
        var activity = Activity.Current;
        return activity is null
            ? new RunnerTraceContext(string.Empty, null)
            : new RunnerTraceContext(activity.Id ?? string.Empty, activity.TraceStateString);
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolvePromptOverridesAsync(
        Guid clientId,
        CancellationToken ct)
    {
        if (promptOverrideService is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in PromptOverride.ValidPromptKeys)
        {
            var text = await promptOverrideService.GetOverrideAsync(clientId, null, key, ct);
            if (text is not null)
            {
                overrides[key] = text;
            }
        }

        return overrides;
    }

    /// <summary>
    ///     How much the job may still spend before its tightest hard cap. An optimisation only: it lets an
    ///     executor wind down rather than be refused mid-pass, and it is stale the moment it is written, so
    ///     the relay stays the point where the cap is actually enforced.
    /// </summary>
    private async Task<decimal?> ResolveBudgetHeadroomAsync(ReviewJob job, CancellationToken ct)
    {
        if (budgetCapsProvider is null || spendAccumulator is null)
        {
            return null;
        }

        var caps = await budgetCapsProvider.GetCapsAsync(job.ClientId, ct);
        if (!caps.AnyHardCapConfigured)
        {
            return null;
        }

        var baseline = await spendAccumulator.GetBaselineAsync(
            ReviewSpendSubject.For(job),
            DateOnly.FromDateTime(DateTime.UtcNow),
            ct);

        decimal? headroom = null;
        Narrow(ref headroom, caps.MonthlyHardCapUsd, baseline.ClientMonthToDate.KnownUsd);
        Narrow(ref headroom, caps.PullRequestHardCapUsd, baseline.PullRequest.KnownUsd);
        Narrow(ref headroom, caps.IncrementHardCapUsd, baseline.Increment.KnownUsd);
        return headroom;
    }

    private static void Narrow(ref decimal? headroom, decimal? cap, decimal spent)
    {
        if (cap is not { } capValue)
        {
            return;
        }

        var remaining = Math.Max(0m, capValue - spent);
        if (headroom is null || remaining < headroom)
        {
            headroom = remaining;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Resolving the runner job manifest for review job {JobId} failed; the job was not offered")]
    private static partial void LogResolutionFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Linked items for review job {JobId} could not be discovered at dispatch; the review proceeds without them")]
    private static partial void LogLinkedItemsSkipped(ILogger logger, Guid jobId, Exception ex);
}
