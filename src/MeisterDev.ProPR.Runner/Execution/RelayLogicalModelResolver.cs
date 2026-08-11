// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     The pipeline's model catalog, on a host that holds no provider credential.
///     <para>
///         In the control plane a pass's model name is looked up in the database and turned into a client
///         bound to a stored key. Here the lookup is against the manifest, and every name resolves to the
///         same relay pointed at a different role. The key stays where it was.
///     </para>
///     <para>
///         Only names the manifest carries resolve. A pass list that changed after dispatch is a
///         configuration change the running review must not see, and a name the manifest never carried is a
///         name the relay would refuse in any case. Failing here reports that while the pass is still in
///         hand.
///     </para>
/// </summary>
public sealed class RelayLogicalModelResolver : ILogicalModelResolver
{
    private readonly Dictionary<string, RunnerModelBinding> _bindings;
    private readonly Func<string, IChatClient> _relay;

    /// <summary>Builds the catalog from the manifest's default model and its pass list.</summary>
    /// <param name="manifest">The manifest whose bindings are resolvable.</param>
    /// <param name="relay">Builds a relay client for one named role.</param>
    public RelayLogicalModelResolver(RunnerJobManifest manifest, Func<string, IChatClient> relay)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        this._relay = relay;
        this._bindings = new Dictionary<string, RunnerModelBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [manifest.DefaultModel.LogicalModelName] = manifest.DefaultModel,
        };

        foreach (var pass in manifest.Passes)
        {
            this._bindings[pass.Model.LogicalModelName] = pass.Model;
        }
    }

    /// <inheritdoc />
    public Task<ResolvedLogicalModelChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        string roleName,
        IProtocolRecorder? recorder = null,
        Guid? protocolId = null,
        CancellationToken ct = default)
    {
        if (!this._bindings.TryGetValue(roleName, out var binding))
        {
            throw new InvalidOperationException(
                $"The manifest for this job carries no model named '{roleName}'. Models are resolved once at "
                + "dispatch, so a name that is not in the manifest is one the review was not configured with.");
        }

        return Task.FromResult(
            new ResolvedLogicalModelChatRuntime(
                new RelayChatRuntime(binding, this._relay(binding.LogicalModelName)),
                binding.LogicalModelName,

                // Which layer resolved the role is a control-plane fact, and the manifest deliberately does
                // not carry it: nothing the executor does depends on it, and the trace it would decorate is
                // written where the resolution actually happened.
                LogicalModelLayer.TenantCatalog,
                ParseEffort(binding.ReasoningEffort)));
    }

    /// <inheritdoc />
    public Task<ResolvedLogicalModelEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        string roleName,
        int? expectedDimensions = null,
        IProtocolRecorder? recorder = null,
        Guid? protocolId = null,
        CancellationToken ct = default)
    {
        // The relay serves chat completions and nothing else. Everything that embeds, meaning deduplication
        // and the semantic screener, runs where findings are published. A request here is therefore a wiring
        // mistake rather than a capability gap, and a stub returning zeros would corrupt a similarity
        // comparison.
        throw new NotSupportedException("A runner relays chat completions; embeddings are computed in the control plane.");
    }

    private static ReviewReasoningEffort ParseEffort(string effort)
    {
        return Enum.TryParse<ReviewReasoningEffort>(effort, ignoreCase: true, out var parsed)
            ? parsed
            : ReviewReasoningEffort.None;
    }
}

/// <summary>
///     One model as the pipeline sees it, described by the manifest and served by the relay.
///     <para>
///         The connection and binding are shaped to satisfy the interface, not to describe anything real:
///         the executor has no connection, and the two identifiers here are empty on purpose so a caller
///         that tried to use one gets an obviously absent value rather than a plausible wrong one.
///     </para>
/// </summary>
public sealed class RelayChatRuntime : IResolvedAiChatRuntime
{
    private readonly RunnerModelBinding _binding;

    public RelayChatRuntime(RunnerModelBinding binding, IChatClient chatClient)
    {
        this._binding = binding;
        this.ChatClient = chatClient;

        this.Model = new AiConfiguredModelDto(
            Guid.Empty,
            binding.RemoteModelId,
            binding.LogicalModelName,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto],
            TokenizerName: binding.TokenizerName,
            MaxInputTokens: binding.MaxInputTokens,
            MaxContextTokens: binding.MaxContextTokens,
            SupportsStructuredOutput: binding.SupportsStructuredOutput,
            SupportsToolUse: binding.SupportsToolUse,
            SupportsPromptCaching: binding.SupportsPromptCaching);

        this.Connection = new AiConnectionDto(
            Guid.Empty,
            null,
            binding.LogicalModelName,
            Enum.TryParse<AiProviderKind>(binding.ProviderKind, ignoreCase: true, out var kind)
                ? kind
                : AiProviderKind.OpenAiCompatible,
            string.Empty,
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [this.Model],
            [],
            new AiVerificationResultDto(AiVerificationStatus.Verified),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        this.Binding = new AiPurposeBindingDto(
            Guid.Empty,
            AiPurpose.ReviewDefault,
            RemoteModelId: binding.RemoteModelId);
    }

    public AiConnectionDto Connection { get; }

    public AiConfiguredModelDto Model { get; }

    public AiPurposeBindingDto Binding { get; }

    public IChatClient ChatClient { get; }

    /// <summary>
    ///     What the relay supports. Whole completions only: a provider-managed session or a background
    ///     response would be held on the control plane's connection rather than this one, and claiming
    ///     either would leave the reviewer waiting for a continuation that never arrives. Prompt caching is
    ///     the exception. The provider behind the relay caches or does not cache regardless of which side
    ///     composed the prompt, so this reports what the manifest says the binding supports. Reporting
    ///     all-false here labelled every remote cache hit as provider_unsupported on the trace.
    /// </summary>
    public AgentReviewRuntimeCapabilities Capabilities =>
        new(false, false, false, false, SupportsPromptCaching: this._binding.SupportsPromptCaching, SupportsPromptCacheRouting: false);

    public string? LogicalModelName => this._binding.LogicalModelName;
}
