// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.AI;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
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
                ParseLevel(binding.ReasoningEffort)));
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
        // The relay serves chat completions and nothing else. Everything that embeds, meaning deduplication,
        // runs where findings are published. A request here is therefore a wiring
        // mistake rather than a capability gap, and a stub returning zeros would corrupt a similarity
        // comparison.
        throw new NotSupportedException("A runner relays chat completions; embeddings are computed in the control plane.");
    }

    /// <summary>The level a manifest names, or null for no level.</summary>
    /// <remarks>
    ///     A control plane writes the level by the provider's name and an empty value for no level. An older control
    ///     plane writes the name of its effort enum, and its <c>None</c> stands for no level; the flag for the level
    ///     none then states what a call without a level sends.
    /// </remarks>
    /// <param name="level">The level as the manifest states it.</param>
    internal static string? ParseLevel(string? level)
    {
        return level is null or "None" ? null : ReasoningLevelSelection.Normalize(level);
    }

    /// <summary>The default level of a manifest's model, also from the flag an older control plane sends.</summary>
    /// <param name="binding">The model as the manifest describes it.</param>
    internal static string? DefaultLevelOf(RunnerModelBinding binding)
    {
        return binding.DefaultReasoningLevel ?? (binding.SupportsReasoningEffortNone ? "none" : null);
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
        ArgumentNullException.ThrowIfNull(binding);

        var family = ProviderFamilyOf(binding);

        this._binding = binding;
        this.ChatClient = chatClient;

        this.Model = new AiConfiguredModelDto(
            binding.ConfiguredModelId ?? Guid.Empty,
            binding.RemoteModelId,
            binding.LogicalModelName,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto],
            TokenizerName: binding.TokenizerName,
            MaxInputTokens: binding.MaxInputTokens,
            MaxContextTokens: binding.MaxContextTokens,
            SupportsStructuredOutput: binding.SupportsStructuredOutput,
            SupportsToolUse: binding.SupportsToolUse,
            SupportsPromptCaching: binding.SupportsPromptCaching,
            DefaultReasoningLevel: RelayLogicalModelResolver.DefaultLevelOf(binding),
            ReasoningLevels: binding.ReasoningLevels);

        this.Connection = new AiConnectionDto(
            binding.ConnectionId ?? Guid.Empty,
            null,
            binding.LogicalModelName,
            family,
            string.Empty,
            ApiKeyShapeOf(family),
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

    /// <summary>
    ///     The authentication mode this family qualifies an API key under, or the bare name where the manifest
    ///     names something that is not a well-formed identity key.
    /// </summary>
    /// <remarks>
    ///     Stated so the synthetic connection carries a shape of the family it names. Nothing authenticates
    ///     through it: the relay calls back through the control plane, which holds the credential.
    /// </remarks>
    /// <param name="family">The identity key the manifest named.</param>
    private static string ApiKeyShapeOf(string family)
    {
        return ProviderVocabulary.IsValidIdentityKey(family)
            ? ProviderVocabulary.Compose(family, "ApiKey")
            : "ApiKey";
    }

    /// <summary>The family the manifest says the call is priced and reported against.</summary>
    /// <param name="binding">The model binding the manifest carried.</param>
    /// <exception cref="InvalidOperationException">The manifest names no family for the model.</exception>
    /// <remarks>
    ///     Carried through as the control plane wrote it. The control plane resolved the family against the
    ///     loaded add-ins before it wrote the manifest, and the relay calls back through that same connection, so
    ///     repeating the resolution here could only disagree with it. A manifest naming no family is refused
    ///     rather than substituted, because every call of the review would then be priced and reported against
    ///     nothing.
    /// </remarks>
    private static string ProviderFamilyOf(RunnerModelBinding binding)
    {
        return string.IsNullOrWhiteSpace(binding.ProviderKind)
            ? throw new InvalidOperationException(
                "The manifest for this job names no provider family for the model "
                + $"'{binding.LogicalModelName}'.")
            : binding.ProviderKind.Trim();
    }

    public AiConnectionDto Connection { get; }

    public AiConfiguredModelDto Model { get; }

    public AiPurposeBindingDto Binding { get; }

    public IChatClient ChatClient { get; }

    /// <inheritdoc />
    public bool SupportsLowReasoningEffort => this._binding.SupportsLowReasoningEffort;

    /// <summary>
    ///     What the relay supports. Whole completions only: a provider-managed session or a background
    ///     response would be held on the control plane's connection rather than this one, and claiming
    ///     either would leave the reviewer waiting for a continuation that never arrives. Prompt caching is
    ///     the exception. The provider behind the relay caches or does not cache regardless of which side
    ///     composed the prompt, so this reports what the manifest says the binding supports. Reporting
    ///     all-false here labelled every remote cache hit as provider_unsupported on the trace.
    /// </summary>
    public AgentReviewRuntimeCapabilities Capabilities =>
        new(
            false,
            false,
            false,
            false,
            SupportsPromptCaching: this._binding.SupportsPromptCaching,
            SupportsPromptCacheRouting: false,
            ReasoningLevels: this._binding.ReasoningLevels,
            DefaultReasoningLevel: RelayLogicalModelResolver.DefaultLevelOf(this._binding),
            ConfiguredReasoningLevel: RelayLogicalModelResolver.ParseLevel(this._binding.ReasoningEffort));

    public string? LogicalModelName => this._binding.LogicalModelName;

    /// <inheritdoc />
    public string? LogicalModelReasoningLevel => RelayLogicalModelResolver.ParseLevel(this._binding.ReasoningEffort);
}
