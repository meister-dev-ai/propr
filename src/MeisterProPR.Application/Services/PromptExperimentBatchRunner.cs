// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Repeats one fixture-backed workflow across a baseline and one or more named prompt variants.
/// </summary>
public sealed class PromptExperimentBatchRunner(
    IReviewWorkflowRunner reviewWorkflowRunner,
    IReviewPromptExperimentValidator promptExperimentValidator,
    IEvaluationArtifactWriter artifactWriter,
    IAiChatClientFactory aiChatClientFactory,
    IProtectedValueResolver protectedValueResolver) : IPromptExperimentBatchRunner
{
    /// <inheritdoc />
    public async Task<PromptExperimentBatchResult> RunAsync(
        PromptExperimentBatch batch,
        ReviewEvaluationFixture fixture,
        EvaluationConfiguration configuration,
        ReviewJob jobTemplate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(jobTemplate);

        await promptExperimentValidator.ValidateAsync(batch, cancellationToken);

        var resolvedSecrets = configuration.ProtectedValueReferencesOrEmpty.Count > 0
            ? await protectedValueResolver.ResolveAsync(configuration.ProtectedValueReferencesOrEmpty, cancellationToken)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var chatClient = this.ResolveChatClient(configuration, resolvedSecrets);
        var artifactPaths = new List<string>(batch.VariantRunsOrEmpty.Count);

        foreach (var run in batch.VariantRunsOrEmpty)
        {
            var skippedSteps = new ReviewStepSkips(run.SkippedStepIdsOrEmpty);
            var promptExperimentContext = new PromptExperimentContext(run.VariantName, run.StageVariantsOrEmpty, skippedSteps);
            var job = CloneJob(jobTemplate);
            var requestConfiguration = configuration with
            {
                Output = configuration.Output with { ArtifactPath = run.ArtifactPath },
                RunMetadata = MergeRunMetadata(configuration.RunMetadataOrEmpty, run.RunMetadataOrEmpty),
            };
            var request = new ReviewWorkflowRequest(
                job,
                chatClient,
                requestConfiguration.ModelSelection.ModelId,
                fixture,
                requestConfiguration,
                PromptExperiment: promptExperimentContext,
                SkippedSteps: skippedSteps);

            var workflowResult = await reviewWorkflowRunner.RunAsync(request, cancellationToken);
            var artifact = CreateArtifact(run, fixture, requestConfiguration, workflowResult, promptExperimentContext);
            artifactPaths.Add(await artifactWriter.WriteAsync(artifact, run.ArtifactPath, cancellationToken));
        }

        return new PromptExperimentBatchResult(batch.BatchId, artifactPaths);
    }

    private IChatClient ResolveChatClient(EvaluationConfiguration configuration, IReadOnlyDictionary<string, string> resolvedSecrets)
    {
        if (configuration.AiConnection is null)
        {
            return NullChatClient.Instance;
        }

        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(configuration.AiConnection.ApiKeyReferenceName))
        {
            resolvedSecrets.TryGetValue(configuration.AiConnection.ApiKeyReferenceName, out apiKey);
        }

        return aiChatClientFactory.CreateClient(
            configuration.AiConnection.EndpointUrl,
            apiKey,
            configuration.AiConnection.Provider);
    }

    private static IReadOnlyDictionary<string, string> MergeRunMetadata(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> runMetadata)
    {
        var merged = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
        foreach (var pair in runMetadata)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static ReviewJob CloneJob(ReviewJob template)
    {
        var clone = new ReviewJob(
            Guid.NewGuid(),
            template.ClientId,
            template.OrganizationUrl,
            template.ProjectId,
            template.RepositoryId,
            template.PullRequestId,
            template.IterationId);
        clone.SetReviewPipelineProfile(template.ReviewPipelineProfileId);
        clone.SetPrContext(template.PrTitle, template.PrRepositoryName, template.PrSourceBranch, template.PrTargetBranch);
        clone.SetReviewRevision(template.ReviewRevisionReference);
        clone.SetProviderReviewContext(template.CodeReviewReference);
        clone.SetProCursorSourceScope(template.ProCursorSourceScopeMode, template.ProCursorSourceIds);

        return clone;
    }

    private static EvaluationArtifact CreateArtifact(
        PromptExperimentRunRequest run,
        ReviewEvaluationFixture fixture,
        EvaluationConfiguration configuration,
        ReviewWorkflowResult workflowResult,
        PromptExperimentContext promptExperiment)
    {
        var job = workflowResult.Job;
        var startedAt = workflowResult.Protocols.Count > 0
            ? workflowResult.Protocols.Min(protocol => protocol.StartedAt)
            : DateTimeOffset.UtcNow;
        var completedAt = workflowResult.Protocols.Count > 0
            ? workflowResult.Protocols.Max(protocol => protocol.CompletedAt)
            : null;

        return new EvaluationArtifact(
            new EvaluationRunMetadata(
                run.RunId,
                startedAt,
                completedAt,
                job.Status == JobStatus.Failed ? "failed" : "completed",
                configuration.ProtectedValueReferencesOrEmpty.Count == 0 ? "not_required" : "resolved"),
            new EvaluationFixtureMetadata(
                fixture.FixtureId,
                fixture.FixtureVersion,
                fixture.Provenance.SourceKind,
                fixture.ActiveScenarioIdOrNull),
            new EvaluationConfigurationMetadata(
                configuration.ConfigurationId,
                configuration.ModelSelection.ModelId,
                configuration.Output.DetailMode,
                CreateDefaultProvenanceCounts(),
                promptExperiment.VariantName,
                promptExperiment.ActiveStageKeys,
                promptExperiment.ActiveStageKeys.Count > 0),
            workflowResult.FinalResult,
            ProjectStageEvidence(workflowResult.Protocols, promptExperiment),
            ProjectTokenUsage(job),
            []);
    }

    private static IReadOnlyDictionary<string, int> CreateDefaultProvenanceCounts()
    {
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["baselineOnly"] = 0,
            ["proRvOnly"] = 0,
            ["both"] = 0,
        };
    }

    private static IReadOnlyList<StageEvidenceRecord> ProjectStageEvidence(
        IReadOnlyList<ReviewJobProtocolDto> protocols,
        PromptExperimentContext promptExperiment)
    {
        return protocols.SelectMany(protocol =>
        {
            var events = protocol.Events.Select(CreateStageEvidenceEvent).ToArray();
            var explicitPromptEvidence = events
                .Where(@event => @event.PromptExperimentEvidence is not null)
                .Select(@event => @event.PromptExperimentEvidence!)
                .ToArray();

            if (explicitPromptEvidence.Length > 0)
            {
                return explicitPromptEvidence.Select((promptEvidence, index) =>
                {
                    var (stageId, label, relatedFilePath) = ResolveStageIdentity(protocol, promptEvidence.StageKey, index);
                    return new StageEvidenceRecord(
                        stageId,
                        label,
                        relatedFilePath,
                        protocol.Outcome ?? "unknown",
                        protocol.IterationCount,
                        protocol.ToolCallCount,
                        protocol.TotalInputTokens,
                        protocol.TotalOutputTokens,
                        protocol.FinalConfidence,
                        protocol.ModelId,
                        protocol.AiConnectionCategory,
                        events,
                        promptEvidence);
                });
            }

            var inferredPromptEvidence = CreatePromptEvidence(protocol, promptExperiment);

            return
            [
                new StageEvidenceRecord(
                    protocol.Label ?? string.Empty,
                    protocol.Label ?? string.Empty,
                    protocol.Label is not null && protocol.Label.Contains('/') ? protocol.Label : null,
                    protocol.Outcome ?? "unknown",
                    protocol.IterationCount,
                    protocol.ToolCallCount,
                    protocol.TotalInputTokens,
                    protocol.TotalOutputTokens,
                    protocol.FinalConfidence,
                    protocol.ModelId,
                    protocol.AiConnectionCategory,
                    events,
                    inferredPromptEvidence),
            ];
        }).ToArray();
    }

    private static StageEvidenceEvent CreateStageEvidenceEvent(ProtocolEventDto @event)
    {
        return new StageEvidenceEvent(
            @event.Kind,
            @event.Name,
            @event.OccurredAt,
            @event.InputTokens,
            @event.OutputTokens,
            @event.InputTextSample,
            @event.SystemPrompt,
            @event.OutputSummary,
            @event.Error,
            TryCreatePromptEvidence(@event),
            @event.CachedInputTokens,
            @event.CacheWriteTokens,
            @event.ReasoningTokens,
            @event.ToolEvidence is null
                ? null
                : new ProtocolToolEvidenceSnapshot(
                    @event.ToolEvidence.SourceToolName,
                    @event.ToolEvidence.OriginalPayloadTokens,
                    @event.ToolEvidence.BoundedPayloadTokens,
                    @event.ToolEvidence.Action,
                    @event.ToolEvidence.Refreshable),
            @event.FinalizationAttemptKind);
    }

    private static PromptExperimentEvidence? CreatePromptEvidence(ReviewJobProtocolDto protocol, PromptExperimentContext promptExperiment)
    {
        var firstAiCall = protocol.Events.FirstOrDefault(@event => @event.Kind == ProtocolEventKind.AiCall);
        if (firstAiCall is null || (string.IsNullOrWhiteSpace(firstAiCall.SystemPrompt) && string.IsNullOrWhiteSpace(firstAiCall.InputTextSample)))
        {
            return null;
        }

        var stageKey = ResolveStageKey(protocol.Label, firstAiCall);
        var hasSystemPrompt = !string.IsNullOrWhiteSpace(firstAiCall.SystemPrompt);
        var role = hasSystemPrompt ? PromptStageRole.System : PromptStageRole.User;
        promptExperiment.TryGetVariant(stageKey, role, out var variant);

        return new PromptExperimentEvidence(
            stageKey,
            promptExperiment.VariantName,
            variant?.CompositionMode ?? PromptCompositionMode.Default,
            variant is null,
            firstAiCall.SystemPrompt,
            firstAiCall.InputTextSample);
    }

    private static PromptExperimentEvidence? TryCreatePromptEvidence(ProtocolEventDto @event)
    {
        if (!string.Equals(@event.Name, "prompt_stage_evidence_recorded", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(@event.OutputSummary))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(@event.OutputSummary);
            var root = document.RootElement;
            var stageKey = root.TryGetProperty("stageKey", out var stageKeyElement) ? stageKeyElement.GetString() : null;
            var variantName = root.TryGetProperty("variantName", out var variantNameElement) ? variantNameElement.GetString() : null;
            var compositionModeValue = root.TryGetProperty("compositionMode", out var compositionModeElement) ? compositionModeElement.GetString() : null;
            var usedDefaultConstruction = root.TryGetProperty("usedDefaultConstruction", out var usedDefaultConstructionElement)
                                          && usedDefaultConstructionElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                                          && usedDefaultConstructionElement.GetBoolean();

            if (string.IsNullOrWhiteSpace(stageKey) || string.IsNullOrWhiteSpace(variantName) || string.IsNullOrWhiteSpace(compositionModeValue))
            {
                return null;
            }

            if (!Enum.TryParse<PromptCompositionMode>(compositionModeValue, true, out var compositionMode))
            {
                return null;
            }

            return new PromptExperimentEvidence(
                stageKey,
                variantName,
                compositionMode,
                usedDefaultConstruction,
                @event.SystemPrompt,
                @event.InputTextSample);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string StageId, string Label, string? RelatedFilePath) ResolveStageIdentity(
        ReviewJobProtocolDto protocol, string stageKey, int occurrenceIndex)
    {
        var relatedFilePath = protocol.Label is not null && protocol.Label.Contains('/') ? protocol.Label : null;
        var baseId = string.IsNullOrWhiteSpace(protocol.Label) ? stageKey : $"{protocol.Label}:{stageKey}";
        var stageId = occurrenceIndex == 0 ? baseId : $"{baseId}:{occurrenceIndex + 1}";

        if (PromptStageCatalog.TryGet(stageKey, out var definition) && definition is not null)
        {
            return (stageId, definition.Label, relatedFilePath);
        }

        return (stageId, stageKey, relatedFilePath);
    }

    private static string ResolveStageKey(string? label, ProtocolEventDto aiCall)
    {
        if (string.Equals(label, "synthesis", StringComparison.OrdinalIgnoreCase))
        {
            return PromptStageKeys.SynthesisSystem;
        }

        if (!string.IsNullOrWhiteSpace(label) && label.Contains('/', StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(aiCall.SystemPrompt)
                ? PromptStageKeys.PerFileUser
                : PromptStageKeys.PerFileContextSystem;
        }

        return string.IsNullOrWhiteSpace(aiCall.SystemPrompt)
            ? PromptStageKeys.PerFileUser
            : PromptStageKeys.GlobalSystem;
    }

    private static EvaluationTokenUsage ProjectTokenUsage(ReviewJob job)
    {
        var breakdown = job.TokenBreakdown;
        var byModel = breakdown
            .GroupBy(entry => entry.ModelId, StringComparer.Ordinal)
            .Select(group => new EvaluationTokenUsageBreakdown(
                group.Key,
                group.Sum(entry => entry.TotalInputTokens),
                group.Sum(entry => entry.TotalOutputTokens)))
            .ToArray();
        var byCategory = breakdown
            .GroupBy(entry => entry.ConnectionCategory.ToString(), StringComparer.Ordinal)
            .Select(group => new EvaluationTokenUsageBreakdown(
                group.Key,
                group.Sum(entry => entry.TotalInputTokens),
                group.Sum(entry => entry.TotalOutputTokens)))
            .ToArray();

        // Derive the top-level totals from the breakdown itself so the artifact stays internally
        // consistent with byModel/byCategory, rather than trusting the job's flat aggregate columns
        // which could be stale or partial if an upstream collector dropped a counter.
        return new EvaluationTokenUsage(
            breakdown.Sum(entry => entry.TotalInputTokens),
            breakdown.Sum(entry => entry.TotalOutputTokens),
            byModel,
            byCategory,
            TotalCachedInputTokens: breakdown.Sum(entry => entry.TotalCachedInputTokens),
            TotalCacheWriteTokens: breakdown.Sum(entry => entry.TotalCacheWriteTokens),
            TotalReasoningTokens: breakdown.Sum(entry => entry.TotalReasoningTokens));
    }

    private sealed class NullChatClient : IChatClient
    {
        public static NullChatClient Instance { get; } = new();

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No evaluation AI connection was configured for this prompt experiment run.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No evaluation AI connection was configured for this prompt experiment run.");
        }
    }
}
