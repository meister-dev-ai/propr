// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>
///     Repeats one fixture-backed workflow across a baseline and one or more named prompt variants.
/// </summary>
public sealed class PromptExperimentBatchRunner(
    IReviewWorkflowRunner reviewWorkflowRunner,
    IReviewPromptExperimentValidator promptExperimentValidator,
    IEvaluationArtifactWriter artifactWriter,
    IAiRuntimeFactory aiRuntimeFactory,
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

    // The harness names its endpoint in a configuration file, so the connection profile the shared construction
    // step takes is assembled here from those values. Building through that step gives a harness run the same
    // driver, retry classification and request shaping a production review gets, so the two are comparable.
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

        var model = HarnessChatModel(configuration.AiConnection.Provider, configuration.ModelSelection.ModelId);
        var binding = new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, model.Id, model.RemoteModelId);
        var connection = HarnessConnection(configuration.AiConnection, apiKey, model, binding);

        return aiRuntimeFactory.CreateChatRuntime(connection, model, binding).ChatClient;
    }

    // The run's primary model, used as the default. Each call still selects its own deployment through
    // ChatOptions.ModelId, so naming one here restricts nothing. Both chat shapes of the configured family are
    // declared supported because the harness configuration file carries no capability metadata to narrow the
    // set with; a family that declares neither is unaffected, since a shape it does not declare narrows nothing.
    private static AiConfiguredModelDto HarnessChatModel(string providerKind, string modelId)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            modelId,
            modelId,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, .. ChatShapesOf(providerKind)],
            SupportsStructuredOutput: true,
            SupportsToolUse: true);
    }

    // The chat shapes a family qualifies under its own key, or none where the configuration names something that
    // is not a well-formed identity key. Composed rather than read from the family, because the harness builds
    // this connection before any driver has been resolved.
    private static IReadOnlyList<string> ChatShapesOf(string providerKind)
    {
        return ProviderVocabulary.IsValidIdentityKey(providerKind)
            ?
            [
                ProviderVocabulary.Compose(providerKind, "Responses"),
                ProviderVocabulary.Compose(providerKind, "ChatCompletions"),
            ]
            : [];
    }

    // The authentication mode a family qualifies under its own key, left as the bare name where the configuration
    // names something that is not a well-formed identity key.
    private static string AuthShapeOf(string providerKind, string modeName)
    {
        return ProviderVocabulary.IsValidIdentityKey(providerKind)
            ? ProviderVocabulary.Compose(providerKind, modeName)
            : modeName;
    }

    private static AiConnectionDto HarnessConnection(
        EvaluationAiConnection aiConnection,
        string? apiKey,
        AiConfiguredModelDto model,
        AiPurposeBindingDto binding)
    {
        // An Azure resource reached without a key authenticates from the ambient credential chain; every other
        // family, and Azure with a key, authenticates with the key the configuration referenced.
        var authMode = string.IsNullOrWhiteSpace(apiKey)
                       && ProviderVocabulary.KeysEqual(aiConnection.Provider, EvaluationAiConnection.AzureOpenAiKey)
            ? AuthShapeOf(aiConnection.Provider, "AzureIdentity")
            : AuthShapeOf(aiConnection.Provider, "ApiKey");

        return new AiConnectionDto(
            HarnessConnectionId(aiConnection),
            Guid.Empty,
            "review-evaluation-harness",
            aiConnection.Provider,
            aiConnection.EndpointUrl,
            authMode,
            AiDiscoveryMode.ManualOnly,
            true,
            [model],
            [binding],
            AiVerificationResultDto.NeverVerified,
            default,
            default,
            Secret: apiKey);
    }

    /// <remarks>
    ///     The runtime keys the throttle gate on the connection's identifier, so two harness connections sharing
    ///     one identifier would pace each other against one provider's quota. It is derived from the endpoint and
    ///     the family instead of generated, so repeated runs against one endpoint go on sharing a gate, which the
    ///     gate needs in order to pace anything at all.
    /// </remarks>
    private static Guid HarnessConnectionId(EvaluationAiConnection aiConnection)
    {
        return StableGuidGenerator.Create($"review-evaluation-harness|{aiConnection.Provider}|{aiConnection.EndpointUrl}");
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
